using System;
using Windows.UI.Xaml;

namespace ClawWidget.UI
{
    /// <summary>
    /// 슬라이더용: 마지막 움직임 후 400 ms 뒤 한 번만 보낸다. 움직인 뒤 잠시(Hold)는 폴링 값으로 덮어쓰지 말라는 신호도 준다.
    /// </summary>
    public sealed class Debouncer
    {
        private static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(2.5);
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private readonly Action _action;
        private DateTime _holdUntil;

        public Debouncer(Action action, int milliseconds = 400)
        {
            _action = action;
            _timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
            _timer.Tick += (s, e) =>
            {
                _timer.Stop();
                _holdUntil = DateTime.UtcNow + HoldTime;
                _action();
            };
        }

        /// <summary>사용자가 값을 바꿨다 — 타이머 재시작.</summary>
        public void Trigger()
        {
            _holdUntil = DateTime.UtcNow + HoldTime;
            _timer.Stop();
            _timer.Start();
        }

        /// <summary>true면 서비스 값으로 컨트롤을 덮어쓰지 말 것 (보내기 전이거나 막 보낸 직후).</summary>
        public bool IsHolding => _timer.IsEnabled || DateTime.UtcNow < _holdUntil;

        public void Stop() => _timer.Stop();
    }
}
