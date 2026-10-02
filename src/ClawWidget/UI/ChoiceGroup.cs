using System;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

namespace ClawWidget.UI
{
    /// <summary>
    /// 누른 선택지를 응답 전에 먼저 표시해 둔 상태를 모든 묶음에서 한꺼번에 푸는 곳.
    /// 명령이 실패하면 페이지가 <see cref="Clear"/>를 불러, 각 묶음이 서비스가 보낸 실제 값으로 돌아가게 한다.
    /// </summary>
    public static class PendingChoices
    {
        public static event Action Cleared;

        public static void Clear() => Cleared?.Invoke();
    }

    /// <summary>
    /// 세그먼트형 선택지 (메트로 타일 묶음, App.xaml MetroChoiceStyle). 패드에서는 XY 포커스로 이동하고 A로 고른다.
    /// 누르면 바로 강조색으로 표시하고(응답을 기다리지 않음 — 위젯이 굼떠 보이지 않게) 명령을 보낸다.
    /// 그동안(최대 2.5초) 폴링으로 온 이전 값은 무시하고, 서비스가 그 값을 확인해 주면 확정한다.
    /// 실패하면 <see cref="PendingChoices.Clear"/>로 풀려 실제 값으로 돌아간다.
    /// </summary>
    public sealed class ChoiceGroup<T>
    {
        private const int PendingMs = 2500;

        private readonly List<KeyValuePair<T, ToggleButton>> _items = new List<KeyValuePair<T, ToggleButton>>();
        private bool _hasSelection;
        private T _selection;
        private bool _hasPending;
        private T _pending;
        private int _pendingAt;
        // 서비스가 마지막으로 알려 준 실제 값 (보류가 풀리면 이것으로 돌아간다)
        private bool _hasActual;
        private T _actual;

        /// <summary>사용자가 이미 선택된 것과 다른 항목을 눌렀을 때.</summary>
        public event Action<T> Chosen;

        public ChoiceGroup(Grid host, int columns, IEnumerable<KeyValuePair<T, object>> items)
        {
            var style = (Style)Application.Current.Resources["MetroChoiceStyle"];

            host.ColumnDefinitions.Clear();
            host.RowDefinitions.Clear();
            for (var c = 0; c < columns; c++) host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var index = 0;
            foreach (var item in items)
            {
                var row = index / columns;
                if (row >= host.RowDefinitions.Count) host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var b = new ToggleButton { Style = style, Content = item.Value };
                Grid.SetRow(b, row);
                Grid.SetColumn(b, index % columns);
                var value = item.Key;
                b.Click += (s, e) =>
                {
                    // ToggleButton은 누를 때마다 스스로 켜고 끄므로 표시는 항상 여기서 다시 맞춘다
                    if (_hasSelection && EqualityComparer<T>.Default.Equals(_selection, value))
                    {
                        Show(true, value);
                        return;
                    }
                    _hasPending = true;
                    _pending = value;
                    _pendingAt = Environment.TickCount;
                    Show(true, value);
                    Chosen?.Invoke(value);
                };
                host.Children.Add(b);
                _items.Add(new KeyValuePair<T, ToggleButton>(value, b));
                index++;
            }

            PendingChoices.Cleared += () =>
            {
                if (!_hasPending) return;
                _hasPending = false;
                Show(_hasActual, _actual);
            };
        }

        /// <summary>서비스 상태로 표시를 맞춘다. 방금 누른 값이 아직 확인되지 않았으면 그것을 계속 보여 준다.</summary>
        public void Select(bool hasValue, T value)
        {
            _hasActual = hasValue;
            _actual = value;
            if (_hasPending)
            {
                var confirmed = hasValue && EqualityComparer<T>.Default.Equals(value, _pending);
                if (!confirmed && unchecked(Environment.TickCount - _pendingAt) < PendingMs) return;
                _hasPending = false;
            }
            Show(hasValue, value);
        }

        private void Show(bool hasValue, T value)
        {
            _hasSelection = hasValue;
            _selection = value;
            foreach (var kv in _items)
            {
                var on = hasValue && EqualityComparer<T>.Default.Equals(kv.Key, value);
                if (kv.Value.IsChecked != on) kv.Value.IsChecked = on;
            }
        }

        public void ClearSelection() => Select(false, default(T));

        public bool IsEnabled
        {
            set { foreach (var kv in _items) kv.Value.IsEnabled = value; }
        }

        public double ItemMinHeight
        {
            set { foreach (var kv in _items) kv.Value.MinHeight = value; }
        }

        /// <summary>선택된 버튼 (없으면 첫 버튼) — 처음 포커스 줄 때.</summary>
        public Control FocusTarget
        {
            get
            {
                foreach (var kv in _items)
                    if (_hasSelection && EqualityComparer<T>.Default.Equals(kv.Key, _selection)) return kv.Value;
                return _items.Count > 0 ? _items[0].Value : null;
            }
        }
    }
}
