using System.Text.Json;
using System.Windows.Threading;
using ClawCore.Controller;
using ClawCore.Mapping;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>
/// 매핑 편집을 돕는 것들: 되돌리기, "눌러서 찾기"(물리 버튼을 누르면 그 줄을 편집), 겹치는 키 경고,
/// 컨트롤러 그림에서 강조할 버튼.
/// </summary>
public sealed partial class MappingEditorViewModel : IPadButtonListener
{
    // ── 컨트롤러 그림 ───────────────────────────────────────

    /// <summary>그림에서 강조할 버튼 (지금 포커스·마우스가 있는 줄 / 편집 중인 줄).</summary>
    [ObservableProperty] private ClawButton? _focusedButton;

    /// <summary>뷰가 줄 포커스·마우스 진입 때 부른다.</summary>
    public void Spotlight(ButtonRowViewModel? row)
    {
        if (!IsPickerOpen) FocusedButton = row?.Button;
    }

    public IEnumerable<ButtonRowViewModel> Rows => AllRows;

    /// <summary>그림에서 버튼을 누름 = 그 줄 편집.</summary>
    [RelayCommand]
    private void EditButton(ClawButton button)
    {
        if (IsTesting || AllRows.FirstOrDefault(r => r.Button == button) is not { } row) return;
        EditRow(row);
    }

    // ── 되돌리기 ────────────────────────────────────────────

    private const int MaxUndo = 40;
    private readonly Stack<string> _undo = new();

    public bool CanUndo => _undo.Count > 0;
    public int UndoCount => _undo.Count;

    /// <summary>바꾸기 직전의 버튼 배치(바인딩·매크로·모드)를 기억한다. 바꾸는 모든 명령의 처음에서 부른다.</summary>
    private void Remember()
    {
        var snapshot = JsonSerializer.Serialize(new Snapshot(Profile.Bindings, Profile.Macros, Profile.Mode), ClawCore.JsonStore.Options);
        if (_undo.TryPeek(out var last) && last == snapshot) return;
        _undo.Push(snapshot);
        if (_undo.Count > MaxUndo)
        {
            // Stack은 아래쪽을 버릴 수 없어서 새로 쌓는다 (드물다)
            var keep = _undo.Take(MaxUndo).Reverse().ToList();
            _undo.Clear();
            foreach (var s in keep) _undo.Push(s);
        }
        NotifyUndo();
    }

    private void NotifyUndo()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoCount));
        UndoCommand.NotifyCanExecuteChanged();
        _shell.UpdateHints();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (!_undo.TryPop(out var json) || JsonSerializer.Deserialize<Snapshot>(json, ClawCore.JsonStore.Options) is not { } s) return;
        Profile.Bindings = s.Bindings ?? [];
        Profile.Macros = s.Macros ?? [];
        Profile.Mode = s.Mode;
        Save();
        MarkKeys();
        OnPropertyChanged(nameof(ModeText));
        NotifyUndo();
        _shell.ShowToast(_undo.Count > 0 ? $"되돌렸어요 · {_undo.Count}번 더 되돌릴 수 있어요" : "되돌렸어요");
    }

    private sealed record Snapshot(
        Dictionary<ClawButton, List<string>>? Bindings,
        Dictionary<ClawButton, List<MappingProfile.MacroStepDef>>? Macros,
        GamepadMode Mode);

    // ── 겹치는 키 ──────────────────────────────────────────

    /// <summary>같은 키(조합)를 내는 다른 버튼이 있으면 줄에 "A와 같은 키"를 띄운다 — 게임에서 어느 버튼인지 구분이 안 된다.</summary>
    private void MarkConflicts()
    {
        foreach (var row in AllRows)
        {
            var keys = Current(row.Button);
            var twins = keys.Count == 0 || row.IsMacro
                ? []
                : AllRows.Where(o => o != row && !o.IsMacro && Current(o.Button).SequenceEqual(keys)).Select(o => o.Short).ToList();
            row.Conflict = twins.Count > 0 ? $"{string.Join("·", twins)}와 같은 키" : null;
        }
    }

    // ── 눌러서 찾기 ────────────────────────────────────────

    private static readonly TimeSpan ListenTimeout = TimeSpan.FromSeconds(6);
    private DispatcherTimer? _listenTimer;

    /// <summary>물리 버튼을 기다리는 중 — 다음에 누른 버튼의 키 선택판을 연다.</summary>
    [ObservableProperty] private bool _isListening;

    public bool IsListeningForPad => IsListening;

    partial void OnIsListeningChanged(bool value) => _shell.UpdateHints();

    [RelayCommand]
    private void ToggleListen()
    {
        if (IsListening) { StopListening(); return; }
        if (IsTesting || IsPickerOpen) return;
        IsListening = true;
        _listenTimer ??= new DispatcherTimer { Interval = ListenTimeout };
        _listenTimer.Tick -= ListenTimedOut;
        _listenTimer.Tick += ListenTimedOut;
        _listenTimer.Stop();
        _listenTimer.Start();
    }

    private void ListenTimedOut(object? sender, EventArgs e) => StopListening();

    private void StopListening()
    {
        _listenTimer?.Stop();
        IsListening = false;
    }

    /// <summary>패드에서 새로 누른 버튼 (GamepadInput.Capture). 찾는 중이면 그 줄을 열고 입력을 가져간다.</summary>
    public bool OnPadButton(ClawButton button)
    {
        if (!IsListening) return false;
        StopListening();
        if (AllRows.FirstOrDefault(r => r.Button == button) is { } row)
        {
            FocusedButton = button;
            EditRow(row);
        }
        return true;
    }
}

/// <summary>패드 물리 버튼을 화면 이동보다 먼저 받고 싶은 페이지 (매핑 편집의 "눌러서 찾기").</summary>
public interface IPadButtonListener
{
    bool IsListeningForPad { get; }
    bool OnPadButton(ClawButton button);
}
