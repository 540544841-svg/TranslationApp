using System.Windows;
using TranslationApp.Core.History;

namespace TranslationApp.Windows;

public partial class EditTranslationWindow : Window
{
    private bool _updatingQualityState;

    public EditTranslationWindow(TranslationRecord record)
    {
        InitializeComponent();
        DataContext = record;
        TranslatedBox.Text = record.TranslatedText;
        _updatingQualityState = true;
        ReviewedBox.IsChecked = record.Reviewed && !record.Rejected;
        RejectedBox.IsChecked = record.Rejected;
        _updatingQualityState = false;
    }

    public string EditedText => TranslatedBox.Text;

    public bool Reviewed => ReviewedBox.IsChecked == true;

    public bool Rejected => RejectedBox.IsChecked == true;

    public bool AddToGlossary => GlossaryBox.IsChecked == true;

    private void OnReviewedChecked(object sender, RoutedEventArgs e)
    {
        if (_updatingQualityState) return;
        _updatingQualityState = true;
        RejectedBox.IsChecked = false;
        _updatingQualityState = false;
    }

    private void OnRejectedChecked(object sender, RoutedEventArgs e)
    {
        if (_updatingQualityState) return;
        _updatingQualityState = true;
        ReviewedBox.IsChecked = false;
        _updatingQualityState = false;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
