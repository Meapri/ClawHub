using System;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace ClawWidget.UI
{
    /// <summary>
    /// 세그먼트형 선택지 (버튼 묶음). 선택된 버튼은 강조 스타일. 패드에서는 일반 버튼처럼 XY 포커스로 이동하고 A로 고른다.
    /// 선택 표시는 서비스 응답으로만 바뀐다 (누르는 순간 바꾸지 않음) — 화면이 실제 하드웨어 상태와 어긋나지 않게.
    /// </summary>
    public sealed class ChoiceGroup<T>
    {
        private readonly List<KeyValuePair<T, Button>> _items = new List<KeyValuePair<T, Button>>();
        private readonly Style _normal;
        private readonly Style _selected;
        private bool _hasSelection;
        private T _selection;

        /// <summary>사용자가 이미 선택된 것과 다른 항목을 눌렀을 때.</summary>
        public event Action<T> Chosen;

        public ChoiceGroup(Grid host, int columns, IEnumerable<KeyValuePair<T, object>> items, string selectedStyleKey = "ChoiceButtonSelectedStyle")
        {
            _normal = (Style)Application.Current.Resources["ChoiceButtonStyle"];
            _selected = (Style)Application.Current.Resources[selectedStyleKey];

            host.ColumnDefinitions.Clear();
            host.RowDefinitions.Clear();
            for (var c = 0; c < columns; c++) host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var index = 0;
            foreach (var item in items)
            {
                var row = index / columns;
                if (row >= host.RowDefinitions.Count) host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var b = new Button { Style = _normal, Content = item.Value };
                Grid.SetRow(b, row);
                Grid.SetColumn(b, index % columns);
                var value = item.Key;
                b.Click += (s, e) =>
                {
                    if (_hasSelection && EqualityComparer<T>.Default.Equals(_selection, value)) return;
                    Chosen?.Invoke(value);
                };
                host.Children.Add(b);
                _items.Add(new KeyValuePair<T, Button>(value, b));
                index++;
            }
        }

        public void Select(bool hasValue, T value)
        {
            _hasSelection = hasValue;
            _selection = value;
            foreach (var kv in _items)
            {
                var on = hasValue && EqualityComparer<T>.Default.Equals(kv.Key, value);
                var style = on ? _selected : _normal;
                if (kv.Value.Style != style) kv.Value.Style = style;
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
