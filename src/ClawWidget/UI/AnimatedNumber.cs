using System;
using System.Collections.Generic;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace ClawWidget.UI
{
    /// <summary>
    /// 숫자가 바뀔 때 이전 값에서 새 값으로 부드럽게 흘러가는 TextBlock (ease-out 350 ms).
    /// 움직이는 숫자가 있을 때만 CompositionTarget.Rendering에 붙는다 (평소엔 프레임 비용 0).
    /// </summary>
    public sealed class AnimatedNumber
    {
        private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(350);
        private static readonly List<AnimatedNumber> Active = new List<AnimatedNumber>();
        private static bool _hooked;

        private readonly TextBlock _text;
        private readonly Func<double, string> _format;
        private readonly string _empty;
        private bool _hasValue;
        private double _from, _to, _current;
        private DateTime _start;

        public AnimatedNumber(TextBlock text, Func<double, string> format, string empty = "–")
        {
            _text = text;
            _format = format;
            _empty = empty;
            _text.Text = empty;
        }

        public void Set(double? value)
        {
            if (!value.HasValue)
            {
                _hasValue = false;
                Active.Remove(this);
                _text.Text = _empty;
                return;
            }
            if (!_hasValue)
            {
                _hasValue = true;
                _current = _to = value.Value;
                _text.Text = _format(_current);
                return;
            }
            if (Math.Abs(value.Value - _to) < 0.0001) return;
            _from = _current;
            _to = value.Value;
            _start = DateTime.UtcNow;
            if (!Active.Contains(this)) Active.Add(this);
            Hook();
        }

        private bool Step(DateTime now)
        {
            var t = (now - _start).TotalMilliseconds / Duration.TotalMilliseconds;
            if (t >= 1) t = 1;
            var eased = 1 - Math.Pow(1 - t, 3);
            _current = _from + (_to - _from) * eased;
            _text.Text = _format(_current);
            return t < 1;
        }

        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            CompositionTarget.Rendering += OnRendering;
        }

        private static void OnRendering(object sender, object e)
        {
            var now = DateTime.UtcNow;
            for (var i = Active.Count - 1; i >= 0; i--)
                if (!Active[i].Step(now)) Active.RemoveAt(i);
            if (Active.Count == 0)
            {
                CompositionTarget.Rendering -= OnRendering;
                _hooked = false;
            }
        }
    }
}
