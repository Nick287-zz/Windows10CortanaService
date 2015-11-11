using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

//“空白页”项模板在 http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409 上有介绍

namespace CortanaService
{
    /// <summary>
    /// 可用于自身或导航至 Frame 内部的空白页。
    /// </summary>
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            this.InitializeComponent();
            this.Loaded += MainPage_Loaded;
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            IPA.Text = IPConfig();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            IPConfig(IPA.Text);
        }

        private string IPConfig(string IP = null)
        {
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;

            if (string.IsNullOrEmpty(IP))
            {
                if (!localSettings.Values.ContainsKey("IPA"))
                {
                    IP = "192.168.1.100";
                    localSettings.Values["IPA"] = IP;
                }
                else
                {
                    IP = localSettings.Values["IPA"].ToString();
                }
            }
            else
            {
                localSettings.Values["IPA"] = IP;
            }

            return IP;
        }
    }
}
