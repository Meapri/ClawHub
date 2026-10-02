using System.Windows.Controls.Primitives;

namespace ClawApp.Views;

/// <summary>
/// 여러 개 중 하나를 고르는 칩 (주사율·해상도·FPS 등). 눌러도 스스로 켜지지 않고, 표시는 IsChecked 바인딩(지금 값)만 따른다.
/// 일반 ToggleButton은 누르는 순간 자기 IsChecked를 뒤집어 단방향 바인딩이 끊기고, 여러 칩이 함께 켜진 채로 남는다.
/// </summary>
public sealed class ChoiceChip : ToggleButton
{
    protected override void OnToggle() { }
}
