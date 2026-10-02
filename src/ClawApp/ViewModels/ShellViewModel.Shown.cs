using CommunityToolkit.Mvvm.ComponentModel;

namespace ClawApp.ViewModels;

/// <summary>
/// 화면 표시 전용: 알림·대화 상자·상태 패널·실행 중 문구가 닫힐 때 마지막 내용을 잠시 더 들고 있는다.
/// 닫힐 때 짧은 퇴장 모션(Motion/Reveal.cs)이 도는 동안 글자가 먼저 비어 버리지 않게 하려는 것이다.
/// 열림·닫힘 판단(입력 경로·포커스)은 원래 속성(Toast·Confirm·StatusPanel·BusyText)을 그대로 쓴다.
/// </summary>
public sealed partial class ShellViewModel
{
    [ObservableProperty] private string? _toastShown;
    [ObservableProperty] private ConfirmRequest? _confirmShown;
    [ObservableProperty] private string? _statusPanelShown;
    [ObservableProperty] private string? _busyShown;

    partial void OnToastChanged(string? value)
    {
        if (value is not null) ToastShown = value;
    }

    partial void OnConfirmChanged(ConfirmRequest? value)
    {
        if (value is not null) ConfirmShown = value;
    }

    partial void OnBusyTextChanged(string? value)
    {
        if (value is not null) BusyShown = value;
    }
}
