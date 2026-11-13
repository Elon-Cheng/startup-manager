using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using StartupManager.Core;
using StartupManager.Storage;
using StartupManager.Windows;

namespace StartupManager.App;

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
}

public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool running;
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        running = true; CommandManager.InvalidateRequerySuggested();
        try { await execute(); }
        catch (Exception e) { MessageBox.Show(e.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { running = false; CommandManager.InvalidateRequerySuggested(); }
    }
}

public sealed class EntryRow(StartupEntry entry, string groupLabel, ImageSource? icon)
{
    public StartupEntry Entry { get; } = entry;
    public string GroupLabel { get; } = groupLabel;
    public ImageSource? Icon { get; } = icon;
    public string ScopeAndTrigger => $"{Entry.Trigger} · {Entry.ScopeLabel}";
    public string LocationLabel => Entry.Source == SourceKind.Registry ? $"{(Entry.Scope == ScopeKind.Machine ? "HKLM" : "HKCU")} · {Entry.RegistryView} 位\n{Entry.Location}\n值名称：{Entry.Name}" : Entry.Location;
    public Brush StateBrush => Entry.State switch
    {
        EntryState.Enabled => Brushes.SeaGreen, EntryState.DisabledByUs => Brushes.SlateGray,
        EntryState.DisabledExternally => Brushes.SlateGray, EntryState.Conflict or EntryState.RecoveryNeeded => Brushes.IndianRed,
        _ => Brushes.DarkGoldenrod
    };
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly StartupService service = new();
    private readonly BackupStore preferences = new(ScopeKind.CurrentUser);
    private List<EntryRow> rows = [];
    private List<string> errors = [];
    private EntryRow? selected;
    private bool busy, showProtected;
    private string search = "", selectedState = "全部状态", selectedSource = "全部来源", status = "准备扫描本机启动项…";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public ICollectionView EntriesView { get; private set; } = CollectionViewSource.GetDefaultView(new List<EntryRow>());
    public string[] States { get; } = ["全部状态", "已开启", "本工具已关闭", "外部已关闭", "状态未知", "配置冲突", "待恢复检查"];
    public string[] Sources { get; } = ["全部来源", "注册表", "启动文件夹", "计划任务"];
    public int TotalCount => rows.Count;
    public int EnabledCount => rows.Count(x => x.Entry.State == EntryState.Enabled);
    public int DisabledCount => rows.Count(x => x.Entry.State == EntryState.DisabledByUs);
    public int UnknownCount => rows.Count(x => x.Entry.State is EntryState.Unknown or EntryState.Conflict or EntryState.RecoveryNeeded);
    public string VisibleSummary => $"显示 {EntriesView.Cast<EntryRow>().Count()} 个入口";
    public Visibility EmptyVisibility => EntriesView.IsEmpty && !busy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BusyVisibility => busy ? Visibility.Visible : Visibility.Collapsed;
    public bool IsBusy { get => busy; private set { busy = value; Notify(); Notify(nameof(BusyVisibility)); Notify(nameof(EmptyVisibility)); CommandManager.InvalidateRequerySuggested(); } }
    public string Status { get => status; private set { status = value; Notify(); } }
    public EntryRow? Selected { get => selected; set { selected = value; Notify(); Notify(nameof(SelectedTitle)); Notify(nameof(DetailNote)); CommandManager.InvalidateRequerySuggested(); } }
    public string SelectedTitle => Selected?.Entry.Name ?? "选择一个启动项";
    public string DetailNote => Selected?.Entry.Note ?? "选中列表中的入口，查看它的启动方式和可执行的操作。关闭自启不会退出正在运行的软件。";
    public string Search { get => search; set { search = value; Notify(); Filter(); } }
    public string SelectedState { get => selectedState; set { selectedState = value; Notify(); Filter(); } }
    public string SelectedSource { get => selectedSource; set { selectedSource = value; Notify(); Filter(); } }
    public bool ShowProtected
    {
        get => showProtected;
        set { showProtected = value; Notify(); Filter(); try { preferences.SaveSetting("showProtected", value.ToString()); } catch (Exception e) { Status = "设置保存失败：" + e.Message; } }
    }
    public ICommand RefreshCommand { get; }
    public ICommand DisableCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand DisableGroupCommand { get; }
    public ICommand ArchiveCommand { get; }
    public ICommand HistoryCommand { get; }
    public ICommand ErrorsCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand OpenLocationCommand { get; }

    public MainViewModel()
    {
        try { showProtected = preferences.GetSetting("showProtected") == "True"; } catch { /* A bad preferences file must not prevent scanning. */ }
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        DisableCommand = new AsyncCommand(() => PerformAsync(OperationKind.Disable, [Selected!.Entry]), () => !IsBusy && Selected?.Entry.CanDisable == true);
        RestoreCommand = new AsyncCommand(() => PerformAsync(Selected!.Entry.CanRestore ? OperationKind.Restore : OperationKind.Enable, [Selected.Entry]),
            () => !IsBusy && (Selected?.Entry.CanRestore == true || Selected?.Entry.CanEnable == true));
        DisableGroupCommand = new AsyncCommand(() => PerformAsync(OperationKind.Disable, GroupCandidates()), () => !IsBusy && GroupCandidates().Count > 1);
        ArchiveCommand = new AsyncCommand(() => PerformAsync(OperationKind.KeepCurrent, [Selected!.Entry]), () => !IsBusy && Selected?.Entry.CanArchive == true);
        HistoryCommand = new AsyncCommand(ShowHistoryAsync, () => !IsBusy);
        ErrorsCommand = new RelayCommand(() => TextDialog.Show("扫描提示", errors.Count == 0 ? "未报告读取错误。\n\n范围：Run 注册表、启动文件夹、开机或登录计划任务。\n注册表和文件夹入口标为状态未知，因为 Windows 自身的禁用设置和策略可能影响执行。\nWindows 内置任务默认隐藏且只读。" : string.Join("\n\n", errors)), () => !IsBusy);
        SettingsCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }));
        OpenLocationCommand = new RelayCommand(OpenLocation, () => Selected != null && !IsBusy && File.Exists(Selected.Entry.Executable));
    }

    private List<StartupEntry> GroupCandidates() => Selected == null ? [] : rows.Where(x => x.Entry.GroupKey == Selected.Entry.GroupKey && x.Entry.CanDisable).Select(x => x.Entry).ToList();

    private void Filter()
    {
        EntriesView.Filter = value => value is EntryRow row && (showProtected || !row.Entry.IsProtected) &&
            (SelectedState == "全部状态" || row.Entry.StateLabel == SelectedState) &&
            (SelectedSource == "全部来源" || row.Entry.SourceLabel == SelectedSource) &&
            (string.IsNullOrWhiteSpace(Search) || (row.Entry.Name + " " + row.Entry.Command + " " + row.Entry.Location).Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase));
        EntriesView.Refresh();
        if (Selected != null && !EntriesView.Contains(Selected)) Selected = null;
        Notify(nameof(VisibleSummary)); Notify(nameof(EmptyVisibility));
    }

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await LoadAsync(); }
        finally { IsBusy = false; }
    }

    private async Task LoadAsync()
    {
        var selectedId = Selected?.Entry.Id;
        Status = "正在读取注册表、启动文件夹和计划任务…";
        var data = await Task.Run(() =>
        {
            var scan = service.Scan();
            var result = new List<EntryRow>();
            var icons = new Dictionary<string, ImageSource?>();
            foreach (var group in scan.Entries.GroupBy(x => x.GroupKey))
            {
                var first = group.First();
                var label = (group.Count() > 1 ? Path.GetFileNameWithoutExtension(first.Executable) : first.Name) +
                    $"  ·  {group.Count()} 个入口  ·  {EntryGrouping.Summarize(group)}";
                foreach (var entry in group)
                {
                    if (!icons.TryGetValue(entry.Executable, out var icon)) { icon = FileIcon.Load(entry.Executable); icons[entry.Executable] = icon; }
                    result.Add(new EntryRow(entry, label, icon));
                }
            }
            return (scan, result);
        });
        rows = data.result; errors = data.scan.Errors;
        EntriesView = CollectionViewSource.GetDefaultView(rows);
        EntriesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(EntryRow.GroupLabel)));
        Notify(nameof(EntriesView));
        Filter();
        Selected = EntriesView.Cast<EntryRow>().FirstOrDefault(x => x.Entry.Id == selectedId) ?? EntriesView.Cast<EntryRow>().FirstOrDefault();
        foreach (var name in new[] { nameof(TotalCount), nameof(EnabledCount), nameof(DisabledCount), nameof(UnknownCount) }) Notify(name);
        Status = $"已扫描 {TotalCount} 个入口 · {errors.Count} 条读取提示 · {DateTime.Now:HH:mm:ss} 更新。未知状态不代表已开启。";
    }

    private async Task PerformAsync(OperationKind action, List<StartupEntry> targets)
    {
        if (IsBusy || targets.Count == 0) return;
        var title = action switch { OperationKind.Disable => "关闭自启", OperationKind.Restore => "恢复原配置", OperationKind.Enable => "开启计划任务", _ => "保留当前配置" };
        var description = string.Join("\n\n", targets.Select(x => $"• {x.Name}\n  {x.SourceLabel} / {x.ScopeLabel}\n  {x.Location}" +
            (x.HasOtherTriggers ? "\n  注意：此操作影响整个任务，包括其他触发器。" : "")));
        description += action == OperationKind.KeepCurrent ? "\n\n将归档旧恢复记录，不修改当前系统配置。原备份文件保留，之后不再显示为待恢复入口。"
            : "\n\n将保存操作记录并核验实际配置。不会卸载软件或结束当前进程。";
        if (targets.Any(x => x.Scope == ScopeKind.Machine) && action != OperationKind.KeepCurrent) description += "\n系统公共入口需要管理员权限，届时会出现 Windows 授权窗口。";
        if (!TextDialog.Confirm(title, description)) return;
        IsBusy = true;
        var results = new List<string>();
        try
        {
            foreach (var target in targets)
            {
                Status = $"正在{title}：{target.Name}…";
                var request = new OperationRequest(action, target);
                OperationResult result;
                try
                {
                    result = target.Scope == ScopeKind.Machine && !StartupService.IsAdministrator
                        ? await ElevationClient.ExecuteAsync(request) : await Task.Run(() => service.Execute(request));
                }
                catch (Exception e) { result = new(false, e.Message); }
                results.Add($"{(result.Success ? "成功" : "未完成")} · {target.Name}\n{result.Message}");
            }
            await LoadAsync();
            TextDialog.Show("操作结果", string.Join("\n\n", results));
        }
        finally { IsBusy = false; }
    }

    private async Task ShowHistoryAsync()
    {
        IsBusy = true;
        try
        {
            var history = await Task.Run(service.History);
            var content = history.Count == 0 ? "还没有操作记录。扫描不会修改启动配置。" : string.Join("\n\n", history.Select(x =>
                $"{DateTimeOffset.Parse(x.Time).ToLocalTime():yyyy-MM-dd HH:mm:ss}  {x.Action} · {x.Result}\n{x.Name}\n{x.Message}"));
            TextDialog.Show("最近 500 条操作记录", content);
        }
        finally { IsBusy = false; }
    }

    private void OpenLocation()
    {
        var path = Selected?.Entry.Executable;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "/select,\"" + path.Replace("\"", "") + "\"", UseShellExecute = true });
    }
}
