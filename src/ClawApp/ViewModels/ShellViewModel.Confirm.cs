using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClawApp.ViewModels;

/// <summary>확인 대화 상자 한 건. Danger면 확인 버튼이 빨간색이고 처음 포커스가 취소에 간다.</summary>
public sealed record ConfirmRequest(string Title, string Message, string ConfirmLabel, string CancelLabel, bool Danger)
{
    public bool HasMessage => !string.IsNullOrEmpty(Message);
}

/// <summary>
/// 앱 안 확인 대화 상자 ("한 번 더 누르면…" 대신). MainWindow가 모든 것 위에 띄우고, 열려 있는 동안
/// 패드·키 입력은 대화 상자만 받는다 (A = 포커스된 버튼, B = 취소, 바깥 누르기 = 취소).
/// 페이지가 바뀌거나 창이 숨거나 본체 버튼을 누르면 취소된 것으로 끝난다.
/// </summary>
public sealed partial class ShellViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmOpen))]
    private ConfirmRequest? _confirm;

    public bool IsConfirmOpen => Confirm is not null;

    private TaskCompletionSource<bool>? _confirmResult;

    /// <summary>
    /// 확인 대화 상자를 띄우고 사용자가 고를 때까지 기다린다. 확인 = true, 취소·B·바깥 누르기 = false.
    /// 이미 열린 대화 상자가 있으면 그것은 취소된다. 창이 숨어 있으면 바로 false.
    /// </summary>
    /// <param name="title">질문 (예: "시스템을 종료할까요?")</param>
    /// <param name="message">덧붙이는 설명. 비우면 제목만.</param>
    /// <param name="confirmLabel">확인 버튼 글자 (예: "종료")</param>
    /// <param name="danger">되돌릴 수 없는 동작: 확인 버튼이 빨간색, 처음 포커스는 취소 버튼.</param>
    /// <param name="cancelLabel">취소 버튼 글자.</param>
    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool danger = false, string cancelLabel = "취소")
    {
        if (!IsWindowVisible) return Task.FromResult(false);
        CancelConfirm();
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirmResult = result;
        Confirm = new ConfirmRequest(title, message, confirmLabel, cancelLabel, danger);
        UpdateHints();
        return result.Task;
    }

    [RelayCommand]
    private void ConfirmAccept() => CloseConfirm(true);

    /// <summary>열린 확인 대화 상자를 취소한다 (없으면 아무 일 없음).</summary>
    [RelayCommand]
    public void CancelConfirm() => CloseConfirm(false);

    private void CloseConfirm(bool accepted)
    {
        if (_confirmResult is not { } result) return;
        _confirmResult = null;
        Confirm = null; // 뷰가 포커스를 원래 자리로 돌려놓은 뒤에
        UpdateHints();
        result.TrySetResult(accepted); // 기다리던 쪽이 이어서 실행된다 (비동기로)
    }

    /// <summary>터치 뒤로 버튼: 홈이 아닌 모든 페이지 (홈은 B를 직접 처리한다).</summary>
    public bool ShowBackButton => CurrentPage is not null and not HomeViewModel;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(CurrentPage):
                OnPropertyChanged(nameof(ShowBackButton));
                CancelConfirm(); // 대화 상자를 띄운 페이지가 아니면 확인할 대상이 없다
                break;
            case nameof(IsWindowVisible) when !IsWindowVisible:
                CancelConfirm();
                break;
        }
    }
}
