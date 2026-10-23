namespace ClawApp.Views;

/// <summary>
/// 포커스가 이 컨트롤 안에 있을 때 B(뒤로)를 먼저 받는다 — 펼친 선택지 접기 등.
/// MainWindow가 포커스된 요소부터 위로 올라가며 찾고, true를 돌려주면 페이지 뒤로 가기는 하지 않는다.
/// </summary>
public interface IBackHandler
{
    bool HandleBack();
}
