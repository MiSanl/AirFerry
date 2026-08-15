using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace AirFerry.Windows.Controls;

/// <summary>
/// Raster-image icon for <c>ui:TitleBar.Icon</c>. WPF-UI 4.3.0 ships no
/// ImageIcon (its <see cref="IconElement"/> subclasses cover glyphs only), and
/// the TitleBar template has no fallback to the Window's Icon — without this
/// element the title bar can only show a generic symbol glyph.
/// </summary>
public class ImageIcon : IconElement
{
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(
            nameof(Source),
            typeof(ImageSource),
            typeof(ImageIcon),
            new PropertyMetadata(null));

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>
    /// IconElement lazily builds its single visual child; bind (rather than
    /// copy) Source so a XAML-time or later assignment keeps working.
    /// </summary>
    protected override UIElement InitializeChildren()
    {
        Image image = new() { Stretch = Stretch.Uniform };
        _ = BindingOperations.SetBinding(
            image, System.Windows.Controls.Image.SourceProperty,
            new Binding(nameof(Source)) { Source = this });
        return image;
    }
}
