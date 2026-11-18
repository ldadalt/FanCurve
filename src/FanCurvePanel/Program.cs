namespace FanCurvePanel;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(PanelPaths.Parse(args)));
        }
        catch (Exception ex)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "panel-error.log");
            File.WriteAllText(path, ex.ToString());
            MessageBox.Show(ex.Message, "FanCurve");
        }
    }
}
