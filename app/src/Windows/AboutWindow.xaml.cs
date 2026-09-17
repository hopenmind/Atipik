using System.Windows;
using Atypik.i18n;

namespace Atypik.Windows;

public partial class AboutWindow : Window
{
    public AboutWindow(bool showLicense = false)
    {
        InitializeComponent();
        ApplyLocalization();
        if (showLicense)
            Title = Loc.T("about.title.license");
    }

    private void ApplyLocalization()
    {
        Title                    = Loc.T("about.title");
        AboutVersion.Text        = Loc.T("about.version");
        AboutSectionEditor.Text  = Loc.T("about.section.editor");
        AboutSectionSymbol.Text  = Loc.T("about.section.symbol");
        AboutSectionLicense.Text = Loc.T("about.section.license");
        CloseBtn.Content         = Loc.T("about.close");
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
