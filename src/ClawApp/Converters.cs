using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClawApp;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is Visibility.Visible) ^ Invert;
}

public sealed class NullToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>값.ToString() == ConverterParameter 이면 Visible.</summary>
public sealed class EqualsToVisibility : IValueConverter
{
    public static readonly EqualsToVisibility Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InvertBool : IValueConverter
{
    public static readonly InvertBool Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>[항목 인덱스, 선택 인덱스] → 같으면 true. 탭 강조용.</summary>
public sealed class TabSelectedConverter : IMultiValueConverter
{
    public static readonly TabSelectedConverter Instance = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is int a && values[1] is int b && a == b;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>페이지가 열릴 때 처음 포커스를 줄 요소 표시.</summary>
public static class Nav
{
    public static readonly DependencyProperty InitialFocusProperty =
        DependencyProperty.RegisterAttached("InitialFocus", typeof(bool), typeof(Nav), new PropertyMetadata(false));

    public static bool GetInitialFocus(DependencyObject d) => (bool)d.GetValue(InitialFocusProperty);
    public static void SetInitialFocus(DependencyObject d, bool value) => d.SetValue(InitialFocusProperty, value);
}
