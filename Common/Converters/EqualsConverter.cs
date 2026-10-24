using System.Globalization;
using System.Windows.Data;
namespace FMLauncher.Common.Converters;
/// <summary>只读选择状态映射，避免导航属性被双向回写。</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Equals(value, parameter);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
