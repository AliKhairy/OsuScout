using System;
using Velopack;

namespace OsuScoutNew
{
    public class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Velopack setup must run before ANY WPF initialization
            VelopackApp.Build().Run();

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }
}
