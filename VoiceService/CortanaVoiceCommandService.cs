using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Globalization;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.Resources.Core;
using Windows.ApplicationModel.VoiceCommands;
using Windows.ApplicationModel.AppService;
using Windows.Storage;
using Windows.ApplicationModel;
using Windows.Web.Http;
using System.Threading;
using System.Diagnostics;
using System.ComponentModel;
using Windows.Foundation;

namespace VoiceService
{
    public sealed class ReturnResult : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Notify any subscribers to the INotifyPropertyChanged interface that a property
        /// was updated. This allows the UI to automatically update (for instance, if Cortana
        /// triggers an update to a trip, or removal of a trip).
        /// </summary>
        /// <param name="propertyName">The case-sensitive name of the property that was updated.</param>
        private void NotifyPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                PropertyChangedEventArgs args = new PropertyChangedEventArgs(propertyName);
                handler(this, args);
            }
        }
    }

    public sealed class CortanaVoiceCommandService : IBackgroundTask
    {
        /// <summary>
        /// the service connection is maintained for the lifetime of a cortana session, once a voice command
        /// has been triggered via Cortana.
        /// </summary>
        VoiceCommandServiceConnection voiceServiceConnection;

        /// <summary>
        /// Lifetime of the background service is controlled via the BackgroundTaskDeferral object, including
        /// registering for cancellation events, signalling end of execution, etc. Cortana may terminate the 
        /// background service task if it loses focus, or the background task takes too long to provide.
        /// 
        /// Background tasks can run for a maximum of 30 seconds.
        /// </summary>
        BackgroundTaskDeferral serviceDeferral;

        /// <summary>
        /// ResourceMap containing localized strings for display in Cortana.
        /// </summary>
        ResourceMap cortanaResourceMap;

        /// <summary>
        /// The context for localized strings.
        /// </summary>
        ResourceContext cortanaContext;

        /// <summary>
        /// Get globalization-aware date formats.
        /// </summary>
        DateTimeFormatInfo dateFormatInfo;

        /// <summary>
        /// Background task entrypoint. Voice Commands using the <VoiceCommandService Target="...">
        /// tag will invoke this when they are recognized by Cortana, passing along details of the 
        /// invocation. 
        /// 
        /// Background tasks must respond to activation by Cortana within 0.5 seconds, and must 
        /// report progress to Cortana every 5 seconds (unless Cortana is waiting for user
        /// input). There is no execution time limit on the background task managed by Cortana,
        /// but developers should use plmdebug (https://msdn.microsoft.com/en-us/library/windows/hardware/jj680085%28v=vs.85%29.aspx)
        /// on the Cortana app package in order to prevent Cortana timing out the task during
        /// debugging.
        /// 
        /// Cortana dismisses its UI if it loses focus. This will cause it to terminate the background
        /// task, even if the background task is being debugged. Use of Remote Debugging is recommended
        /// in order to debug background task behaviors. In order to debug background tasks, open the
        /// project properties for the app package (not the background task project), and enable
        /// Debug -> "Do not launch, but debug my code when it starts". Alternatively, add a long
        /// initial progress screen, and attach to the background task process while it executes.
        /// </summary>
        /// <param name="taskInstance">Connection to the hosting background service process.</param>
        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            serviceDeferral = taskInstance.GetDeferral();

            // Register to receive an event if Cortana dismisses the background task. This will
            // occur if the task takes too long to respond, or if Cortana's UI is dismissed.
            // Any pending operations should be cancelled or waited on to clean up where possible.
            taskInstance.Canceled += OnTaskCanceled;

            var triggerDetails = taskInstance.TriggerDetails as AppServiceTriggerDetails;

            // Load localized resources for strings sent to Cortana to be displayed to the user.
            cortanaResourceMap = ResourceManager.Current.MainResourceMap.GetSubtree("Resources");

            // Select the system language, which is what Cortana should be running as.
            cortanaContext = ResourceContext.GetForViewIndependentUse();

            var lang = Windows.Media.SpeechRecognition.SpeechRecognizer.SystemSpeechLanguage.LanguageTag;

            cortanaContext.Languages = new string[] { Windows.Media.SpeechRecognition.SpeechRecognizer.SystemSpeechLanguage.LanguageTag };

            // Get the currently used system date format
            dateFormatInfo = CultureInfo.CurrentCulture.DateTimeFormat;

            // This should match the uap:AppService and VoiceCommandService references from the 
            // package manifest and VCD files, respectively. Make sure we've been launched by
            // a Cortana Voice Command.
            if (triggerDetails != null && triggerDetails.Name == "CortanaVoiceCommandService")
            {
                try
                {
                    voiceServiceConnection =
                        VoiceCommandServiceConnection.FromAppServiceTriggerDetails(
                            triggerDetails);

                    voiceServiceConnection.VoiceCommandCompleted += OnVoiceCommandCompleted;

                    VoiceCommand voiceCommand = await voiceServiceConnection.GetVoiceCommandAsync();

                    // Depending on the operation (defined in AdventureWorks:AdventureWorksCommands.xml)
                    // perform the appropriate command.
                    switch (voiceCommand.CommandName)
                    {
                        case "Temperature":
                            //var destination = voiceCommand.Properties["destination"][0]; 
                            var condition = voiceCommand.Properties["condition"][0];
                            await ShowInfomation(condition);
                            break;
                        default:
                            // As with app activation VCDs, we need to handle the possibility that
                            // an app update may remove a voice command that is still registered.
                            // This can happen if the user hasn't run an app since an update.
                            LaunchAppInForeground();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Handling Voice Command failed " + ex.ToString());
                }
            }
        }

        /// <summary>
        /// Show a progress screen. These should be posted at least every 5 seconds for a 
        /// long-running operation, such as accessing network resources over a mobile 
        /// carrier network.
        /// </summary>
        /// <param name="message">The message to display, relating to the task being performed.</param>
        /// <returns></returns>
        private async Task ShowProgressScreen(string message)
        {
            var userProgressMessage = new VoiceCommandUserMessage();
            userProgressMessage.DisplayMessage = userProgressMessage.SpokenMessage = message;

            VoiceCommandResponse response = VoiceCommandResponse.CreateResponse(userProgressMessage);
            await voiceServiceConnection.ReportProgressAsync(response);
        }


        /// <summary>
        /// Search for, and show details related to a single trip, if the trip can be
        /// found. This demonstrates a simple response flow in Cortana.
        /// </summary>
        /// <param name="destination">The destination, expected to be in the phrase list.</param>
        /// <returns></returns>
        private async Task ShowInfomation(string condition)
        {
            // If this operation is expected to take longer than 0.5 seconds, the task must
            // provide a progress response to Cortana prior to starting the operation, and
            // provide updates at most every 5 seconds.
            string loadingTripToDestination = string.Format(
                        cortanaResourceMap.GetValue("LoadingInsiderCondition", cortanaContext).ValueAsString,
                        condition);

            await ShowProgressScreen(loadingTripToDestination);

            var userMessage = new VoiceCommandUserMessage();
            var destinationsContentTiles = new List<VoiceCommandContentTile>();


            // file in tiles for each destination, to display information about the trips without
            // launching the app.
            string result = await GetContent("hello");
            char[] seprator = { ';' };
            string[] results = result.Split(seprator);
            if (results.Length == 1)
            {
                var destinationTile = new VoiceCommandContentTile();

                destinationTile.ContentTileType = VoiceCommandContentTileType.TitleWith68x68IconAndText;
                destinationTile.Image = await Package.Current.InstalledLocation.GetFileAsync("VoiceService\\Images\\weather.png");
                destinationTile.Title = "错误信息";
                destinationTile.TextLine1 = result;
                destinationsContentTiles.Add(destinationTile);
            }
            else
            {
                for (int i = 0; i < 3 && i < results.Length; i++)
                {
                    var destinationTile = new VoiceCommandContentTile();

                    destinationTile.ContentTileType = VoiceCommandContentTileType.TitleWith68x68IconAndText;
                    destinationTile.Image = await Package.Current.InstalledLocation.GetFileAsync("VoiceService\\Images\\weather.png");

                    //string Destination = string.Format("地点{0}", i);
                    //destinationTile.AppLaunchArgument = string.Format("destination={0}", Destination);
                    switch (i)
                    {
                        case 0:
                            destinationTile.Title = "摄氏度";
                            destinationTile.TextLine1 = (results[i]) + "℃";
                            destinationsContentTiles.Add(destinationTile);
                            break;
                        case 1:
                            destinationTile.Title = "华氏度";
                            destinationTile.TextLine1 = (results[i]) + "℉";
                            destinationsContentTiles.Add(destinationTile);
                            break;
                        case 2:
                            destinationTile.Title = "湿度";
                            destinationTile.TextLine1 = (results[i]) + "%RH";
                            destinationsContentTiles.Add(destinationTile);
                            break;
                    }
                }
            }

            //message = cortanaResourceMap.GetValue("currentCondition", cortanaContext).ValueAsString;
            double temp = double.Parse(results[0]);
            if (temp < 28)
            {
                try
                {
                    userMessage.DisplayMessage = "库房状态正常";
                    userMessage.SpokenMessage = "库房温湿度正常";

                    var response = VoiceCommandResponse.CreateResponse(userMessage, destinationsContentTiles);
                    //response.AppLaunchArgument = string.Format(destination);
                    await voiceServiceConnection.ReportSuccessAsync(response);
                }
                catch (Exception ex)
                {
                    string meg = ex.Message;
                    Debug.WriteLine(meg);
                }
            }
            else
            {
                VoiceCommandResponse response;
                var userPrompt = new VoiceCommandUserMessage();
                // Prompt the user for confirmation that we've selected the correct trip to cancel.
                string cancelTripToDestination = "气温超过28度，是否打开风扇？";
                userPrompt.DisplayMessage = userPrompt.SpokenMessage = cancelTripToDestination;
                var userReprompt = new VoiceCommandUserMessage();
                string confirmCancelTripToDestination = "超过28度，是否打开风扇？";
                userReprompt.DisplayMessage = userReprompt.SpokenMessage = confirmCancelTripToDestination;

                //REVERT

                var cancelledContentTiles = new List<VoiceCommandContentTile>();
                //cancelledContentTiles.Add(destinationTile);

                response = VoiceCommandResponse.CreateResponseForPrompt(userPrompt, userReprompt, cancelledContentTiles);
                string parameter = "fanopen";
                try
                {
                    var voiceCommandConfirmation = await voiceServiceConnection.RequestConfirmationAsync(response);
                    if (voiceCommandConfirmation != null)
                    {
                        if (voiceCommandConfirmation.Confirmed == true)
                        {
                            userMessage = new VoiceCommandUserMessage();
                            string stropen = "正在打开风扇";
                            await ShowProgressScreen(stropen);
                            string res = await GetContent(parameter);
                            if (string.IsNullOrEmpty(res))
                            {
                                string cancelledTripToDestination = "风扇好像有点问题请稍后再试";
                                userMessage.DisplayMessage = userMessage.SpokenMessage = cancelledTripToDestination;
                                response = VoiceCommandResponse.CreateResponse(userMessage, cancelledContentTiles); //REVERT cancelledContentTiles
                                response.AppLaunchArgument = "打开风扇"; //REVERT
                            }
                            else
                            {
                                string cancelledTripToDestination = "风扇已打开，请放心吧。";
                                userMessage.DisplayMessage = userMessage.SpokenMessage = cancelledTripToDestination;
                                response = VoiceCommandResponse.CreateResponse(userMessage, cancelledContentTiles); //REVERT cancelledContentTiles
                                response.AppLaunchArgument = "打开风扇"; //REVERT
                            }
                        }
                        else
                        {
                            userMessage = new VoiceCommandUserMessage();
                            string keepingTripToDestination = "好吧，那就随它去吧！";
                            userMessage.DisplayMessage = userMessage.SpokenMessage = keepingTripToDestination;

                            response = VoiceCommandResponse.CreateResponse(userMessage);
                            response.AppLaunchArgument = "cancel"; //REVERT
                        }
                        await voiceServiceConnection.ReportSuccessAsync(response);
                    }
                }
                catch (Exception ex)
                {
                    string meg = ex.Message;
                    Debug.WriteLine(meg);
                }


            }

        }

        /// <summary>
        /// Provide a simple response that launches the app. Expected to be used in the
        /// case where the voice command could not be recognized (eg, a VCD/code mismatch.)
        /// </summary>
        private async void LaunchAppInForeground()
        {
            var userMessage = new VoiceCommandUserMessage();
            userMessage.SpokenMessage = cortanaResourceMap.GetValue("LaunchingAdventureWorks", cortanaContext).ValueAsString;

            var response = VoiceCommandResponse.CreateResponse(userMessage);

            response.AppLaunchArgument = "";

            await voiceServiceConnection.RequestAppLaunchAsync(response);
        }

        /// <summary>
        /// Handle the completion of the voice command. Your app may be cancelled
        /// for a variety of reasons, such as user cancellation or not providing 
        /// progress to Cortana in a timely fashion. Clean up any pending long-running
        /// operations (eg, network requests).
        /// </summary>
        /// <param name="sender">The voice connection associated with the command.</param>
        /// <param name="args">Contains an Enumeration indicating why the command was terminated.</param>
        private void OnVoiceCommandCompleted(VoiceCommandServiceConnection sender, VoiceCommandCompletedEventArgs args)
        {
            if (this.serviceDeferral != null)
            {
                this.serviceDeferral.Complete();
            }
        }

        /// <summary>
        /// When the background task is cancelled, clean up/cancel any ongoing long-running operations.
        /// This cancellation notice may not be due to Cortana directly. The voice command connection will
        /// typically already be destroyed by this point and should not be expected to be active.
        /// </summary>
        /// <param name="sender">This background task instance</param>
        /// <param name="reason">Contains an enumeration with the reason for task cancellation</param>
        private void OnTaskCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
        {
            System.Diagnostics.Debug.WriteLine("Task cancelled, clean up");
            if (this.serviceDeferral != null)
            {
                //Complete the service deferral
                this.serviceDeferral.Complete();
            }
        }

        #region tool
        /// <summary>
        /// cortana canvas receive string less than 100 characters;
        /// </summary>
        /// <param name="source">source string</param>
        /// <returns></returns>
        private string Trim100(string source)
        {
            if (String.IsNullOrEmpty(source))
            {
                return String.Empty;
            }
            else
            {
                if (source.Contains("."))
                {
                    return source.Substring(0, source.IndexOf(".") + 3);
                }
            }
            return source;
        }
        #endregion

        private async Task<string> GetContent(string str)
        {
            string result = null;
            var _httpClient = new HttpClient();
            try
            {
                var postData = new HttpFormUrlEncodedContent(
                new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("param1",str),
                }
            );
                var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
                string IP = localSettings.Values["IPA"].ToString();
                HttpResponseMessage response = await _httpClient.PostAsync(new Uri("http://" + IP + ":0808/"), postData);

                if ((int)response.StatusCode != 200)
                {
                    result = string.Format("连接失败，状态码为:{0}", ((int)response.StatusCode));
                }
                else
                {
                    result = await response.Content.ReadAsStringAsync();
                }

                _httpClient.Dispose();

            }
            catch (Exception ex) { result = ex.Message; }

            Debug.WriteLine(result);

            return result;
        }
    }
}
