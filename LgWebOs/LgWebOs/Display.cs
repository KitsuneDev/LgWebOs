using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronIO;
using Guss.Communications.Sockets;
using Guss.Communications.ModuleFramework.Logging;
using Guss.ModuleFramework;
using Guss.ModuleFramework.Events;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XSigUtilityLibrary;
using LgWebOs.Utils;

namespace LgWebOs
{
    public class Display : IDisposable
    {
        #region Private Variables
        private readonly WebSocketClient _wsClient;
        private readonly ILogger _logger;
        private readonly object _mainLock = new object();
        private InputControls _inputControls;
        private List<ExternalInput> _externalInputs;
        private List<App> _apps;
        private readonly CTimer _cmdQueueDequeuer;
        private readonly CTimer _heartbeatTimer;
        private readonly CTimer _heartbeatFailedTimer;
        private readonly CommandQueue<Command> _cmdQueue = new CommandQueue<Command>();

        private ushort _port;
        private string _id;
        private string _ipAddress;
        private string _macAddress;
        private string _clientKey;
        private string _keyFilePath;
        private string _currentInput;
        #endregion

        #region Events

        public event UShortEventHandler PowerStateChanged;
        public event UShortEventHandler VolumeValueChanged;
        public event UShortEventHandler VolumeMuteStateChanged;
        public event UShortEventHandler CurrentInputValueChanged;
        public event UShortEventHandler InputCountChanged;
        public event StringEventHandler ExternalInputNamesChanged;
        public event StringEventHandler ExternalInputIconsChanged;
        public event UShortEventHandler AppCountChanged;
        public event StringEventHandler AppNamesChanged;
        public event StringEventHandler AppIconsChanged;
        #endregion

        #region Public Variables

        public bool Disposed { get; private set; }

        public ushort DebugMode { get { return Convert.ToUInt16(_logger.DebugLevel); } set { _logger.DebugLevel = (DebugLevels)value; } }

        public bool IsInitialized { get; private set; }

        public bool IsRegistered { get; private set; }

        public bool IsPoweredOn { get; private set; }

        public string CurrentInput
        {
            get { return _currentInput; }
        }

        #endregion

        public Display()
        {
            _logger = new Logger("LgWebOs");

            _cmdQueueDequeuer = new CTimer(x =>
            {
                if (_cmdQueue.IsEmpty) return;

                var cmd = _cmdQueue.Dequeue();

                _wsClient.SendCommand(cmd.CommandString);
            }, Timeout.Infinite);

            _heartbeatFailedTimer = new CTimer(x =>
            {
                _logger.LogWarning("Hearbeat timed out, resetting connection.");
                ResetConnection();
            }, Timeout.Infinite);
            _heartbeatTimer = new CTimer(x =>
            {
                SendCommand(new Command(CommandPriorities.High, KeyUtils.GetVerifyClientKey(_clientKey)));
                lock (_mainLock)
                {
                    _heartbeatFailedTimer.Reset(2500);
                }
            }, Timeout.Infinite);

            _wsClient = new WebSocketClient(_logger);

            _wsClient.ConnectedChange += _wsClient_ConnectedChange;
            _wsClient.ResponseReceived += _wsClient_ResponseReceived;
        }

        #region General Methods
        public void Initialize(string id, string ipAddress, ushort port, string macAddress)
        {
            lock (_mainLock)
            {
                if (IsInitialized)
                    return;

                _id = id;
                _ipAddress = ipAddress;
                _port = port;
                _macAddress = Regex.Replace(macAddress, "[-|:]", "");

                
                var currentDirectory = Directory.GetApplicationRootDirectory();

                _logger.LogNotice("Current Directory: {0}", currentDirectory);

                switch (CrestronEnvironment.DevicePlatform)
                {
                    case eDevicePlatform.Appliance:
                    {
                        var currentAppDirectoryArr = Directory.GetApplicationDirectory().Contains("\\")
                            ? Directory.GetApplicationDirectory().Split('\\')
                            : Directory.GetApplicationDirectory().Split('/');

                        _keyFilePath = string.Format(@"{0}User{0}{1}{0}lgWebOsDisplay_{2}",
                            currentDirectory.Contains("\\") ? "\\" : "/", currentAppDirectoryArr[2], _id);
                    }
                        break;
                    case eDevicePlatform.Server:
                        _keyFilePath = string.Format(@"{0}/User/lgWebOsDisplay_{1}", currentDirectory, _id);
                        break;
                    default:
                        return;
                }

                _logger.LogNotice("Key File Path: {0}", _keyFilePath);

                if (File.Exists(_keyFilePath))
                {
                    using (var reader = new StreamReader(File.OpenRead(_keyFilePath)))
                    {
                        _clientKey = reader.ReadToEnd();
                    }
                }

                _wsClient.IpAddress = "ws://" + ipAddress;
                _wsClient.Port = port;

                _cmdQueueDequeuer.Reset(0, 250);

                IsInitialized = true;

                OnUShortEvent(PowerStateChanged, new UShortEventArgs(0));

                _wsClient.Connect();
            }
        }

        private void ResetHeartbeat(long dueTime)
        {
            lock (_mainLock)
            {
                _logger.PrintLine("Restarting heartbeat timer...");
                _heartbeatFailedTimer.Stop();
                _heartbeatTimer.Reset(dueTime);
            }
        }

        private void _wsClient_ResponseReceived(object sender, Guss.Communications.CommunicationsStringEventArgs args)
        {
            try
            {
                ResetHeartbeat(5000);
                _logger.PrintLine("Response received -->{0}<--", args.Payload);
                
                var response = JObject.Parse(args.Payload);

                if (response["type"] == null) return;
                if (response["type"].ToObject<string>() == "registered")
                {
                    if (response["id"] == null) return;
                    switch (response["id"].ToObject<string>())
                    {
                        case "register_0":
                            if (response["payload"]["client-key"] != null)
                            {     
                                lock (_mainLock)
                                {
                                    _clientKey = response["payload"]["client-key"].ToObject<string>();

                                    using (var writer = new StreamWriter(File.Create(_keyFilePath)))
                                    {
                                        writer.Write(_clientKey);
                                    }
                                }

                                ResetHeartbeat(0);
                            }
                            break;
                        case "register_1":
                            if (!IsRegistered)
                            {
                                IsRegistered = true;
                                DisplayGetInfo();
                                ResetHeartbeat(60000);
                            }
                            break;
                        default:
                            _logger.LogWarning("Invalid register response -->{0}<--", args.Payload);
                            break;
                    }
                }
                else
                {
                    if (response["type"].ToObject<string>() != "response") return;
                    if (response["id"] == null) return;
                    switch (response["id"].ToObject<string>())
                    {
                        case "powerOff":
                            if (response["payload"] == null) return;
                            if (response["payload"]["returnValue"].ToObject<bool>())
                            {
                                ResetConnection();
                            }
                            break;
                        case "getInputSocket":
                            lock (_mainLock)
                            {
                                _inputControls = new InputControls(_ipAddress, _port,
                                    response["payload"]["socketPath"].ToObject<string>(), _logger);
                            }
                            SendCommand(new Command(CommandPriorities.Low,
                                "{\"type\":\"request\",\"id\":\"getVolume\",\"uri\":\"ssap://audio/getVolume\"}"));
                            break;
                        default:
                            if (response["id"].ToObject<string>().Contains("changeInput_"))
                            {
                                if (!response["payload"]["returnValue"].ToObject<bool>()) return;

                                ExternalInput input;
                                ushort index;
                                lock (_mainLock)
                                {
                                    _currentInput = response["id"].ToObject<string>()
                                        .Replace("changeInput_", string.Empty);

                                    input = _externalInputs.Find(x => x.Id == _currentInput);
                                    index = Convert.ToUInt16(_externalInputs.IndexOf(input));
                                }

                                if (input == null) return;

                                OnUShortEvent(CurrentInputValueChanged, new UShortEventArgs(index));
                            }
                            else
                                switch (response["id"].ToObject<string>())
                                {
                                    case "getExternalInputs":
                                    {
                                        var inputNames = new List<string>();
                                        var inputIcons = new List<string>();
                                        ushort count;
                                        lock (_mainLock)
                                        {
                                            _externalInputs =
                                                JsonConvert.DeserializeObject<List<ExternalInput>>(
                                                    response["payload"]["devices"].ToString());

                                            foreach (var input in _externalInputs)
                                            {
                                                inputNames.Add(input.Label);
                                                inputIcons.Add(input.Icon.Replace("http:",
                                                    string.Format("http://{0}:{1}", _ipAddress, _port)));
                                            }

                                            count = Convert.ToUInt16(_externalInputs.Count);
                                        }

                                        OnUShortEvent(InputCountChanged, new UShortEventArgs(count));


                                        foreach (var encodedBytes in inputNames.Select(
                                            inputName =>
                                                XSigHelpers.GetBytes(inputNames.IndexOf(inputName) + 1,
                                                    inputName))
                                            )
                                        {

                                            OnStringEvent(ExternalInputNamesChanged,
                                                new StringEventArgs(Encoding.GetEncoding(28591)
                                                    .GetString(encodedBytes, 0, encodedBytes.Length)));
                                        }

                                        foreach (
                                            var encodedBytes in
                                                inputIcons.Select(
                                                    inputIcon => XSigHelpers.GetBytes(inputIcons.IndexOf(inputIcon) + 1,
                                                        inputIcon)))
                                        {

                                            OnStringEvent(ExternalInputIconsChanged, new StringEventArgs(Encoding.GetEncoding(28591)
                                                .GetString(encodedBytes, 0, encodedBytes.Length)));
                                        }
                                    }
                                        break;
                                    case "getAllApps":
                                    {
                                        _apps =
                                            JsonConvert.DeserializeObject<List<App>>(
                                                response["payload"]["launchPoints"].ToString());

                                        var appNames = new List<string>();
                                        var appIcons = new List<string>();

                                        foreach (var input in _apps)
                                        {
                                            appNames.Add(input.Title);
                                            appIcons.Add(input.Icon.Replace("http:",
                                                string.Format("http://{0}:{1}", _ipAddress, _port)));
                                        }

                                        OnUShortEvent(AppCountChanged, new UShortEventArgs(Convert.ToUInt16(_apps.Count)));


                                        foreach (var encodedBytes in appNames.Select(
                                            appName => XSigHelpers.GetBytes(appNames.IndexOf(appName) + 1,
                                                appName)))
                                        {

                                            OnStringEvent(AppNamesChanged, new StringEventArgs(Encoding.GetEncoding(28591)
                                                .GetString(encodedBytes, 0, encodedBytes.Length)));
                                        }


                                        foreach (var encodedBytes in appIcons.Select(
                                            appIcon => XSigHelpers.GetBytes(appIcons.IndexOf(appIcon) + 1,
                                                appIcon)))
                                        {
                                            OnStringEvent(AppIconsChanged, new StringEventArgs(Encoding.GetEncoding(28591)
                                                .GetString(encodedBytes, 0, encodedBytes.Length)));
                                        }
                                    }
                                        break;
                                    case "setVolume":
                                        if (response["payload"]["returnValue"].ToObject<bool>())
                                        {
                                            SendCommand(new Command(CommandPriorities.Low,
                                                "{\"type\":\"request\",\"id\":\"getVolume\",\"uri\":\"ssap://audio/getVolume\"}"));
                                        }
                                        break;
                                    case "volumeUp":
                                        if (response["payload"]["returnValue"].ToObject<bool>())
                                        {
                                            SendCommand(new Command(CommandPriorities.Low,
                                                "{\"type\":\"request\",\"id\":\"getVolume\",\"uri\":\"ssap://audio/getVolume\"}"));
                                        }
                                        break;
                                    case "volumeDown":
                                        if (response["payload"]["returnValue"].ToObject<bool>())
                                        {
                                            SendCommand(new Command(CommandPriorities.Low,
                                                "{\"type\":\"request\",\"id\":\"getVolume\",\"uri\":\"ssap://audio/getVolume\"}"));
                                        }
                                        break;
                                    case "getVolume":
                                        if (!response["payload"]["returnValue"].ToObject<bool>()) return;
                                        var value =
                                            ScaleUp(Convert.ToInt16(response["payload"]["volume"].ToObject<string>()));

                                        OnUShortEvent(VolumeValueChanged, new UShortEventArgs(Convert.ToUInt16(value)));

                                        if (response["payload"]["muted"].ToObject<bool>())
                                        {
                                            OnUShortEvent(VolumeMuteStateChanged, new UShortEventArgs(1));
                                        }
                                        else if (!response["payload"]["muted"].ToObject<bool>())
                                        {
                                            OnUShortEvent(VolumeMuteStateChanged, new UShortEventArgs(0));
                                        }
                                        break;
                                    case "volumeMuteOn":
                                        if (response["payload"]["returnValue"].ToObject<bool>())
                                        {
                                            OnUShortEvent(VolumeMuteStateChanged, new UShortEventArgs(1));
                                        }
                                        break;
                                    case "volumeMuteOff":
                                        if (response["payload"]["returnValue"].ToObject<bool>())
                                        {
                                            OnUShortEvent(VolumeMuteStateChanged, new UShortEventArgs(0));
                                        }
                                        break;
                                }
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogException(ex);
            }
        }

        private void _wsClient_ConnectedChange(object sender, Guss.Communications.CommunicationsBoolEventArgs args)
        {
            _logger.PrintLine("Connection event received {0}", args.Payload);
            _logger.LogNotice("Connection event received {0}", args.Payload);
            lock (_mainLock)
            {
                try
                {
                    if (args.Payload == 1)
                    {
                        _logger.PrintLine("Received connected event!");
                        _logger.LogNotice("Received connected event!");

                        if (!IsPoweredOn)
                        {
                            IsPoweredOn = true;

                            OnUShortEvent(PowerStateChanged, new UShortEventArgs(1));

                            _heartbeatTimer.Reset(0, 10000);
                        }
                        
                        _logger.PrintLine("Processed connected event!");
                        _logger.LogNotice("Processed connected event!");
                    }
                    else
                    {
                        _logger.PrintLine("Received disconnected event!");
                        _logger.LogNotice("Received disconnected event!");


                        if (IsPoweredOn)
                        {
                            IsPoweredOn = false;
                            IsRegistered = false;

                            _heartbeatTimer.Stop();
                            _heartbeatFailedTimer.Stop();

                            _cmdQueue.Clear();

                            if (_inputControls != null)
                            {
                                _inputControls.Dispose();
                            }

                            OnUShortEvent(PowerStateChanged, new UShortEventArgs(0));
                        }

                        _logger.PrintLine("Processed disconnected event!");
                        _logger.LogNotice("Processed disconnected event!");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex);
                }
            }
        }

        public void ResetConnection()
        {
            CrestronInvoke.BeginInvoke(x =>
            {
                lock (_mainLock)
                {
                    _heartbeatTimer.Stop();
                    _heartbeatFailedTimer.Stop();

                    _wsClient.Disconnect();

                    using(var ev = new CEvent(false, false))
                    // ReSharper disable once AccessToDisposedClosure
                    using (new CTimer(_ => ev.Set(), 2500))
                    {
                        ev.Wait();
                    }

                    _wsClient.Connect();
                }
            });
        }

        public void SendCommand(Command commandToSend)
        {
            _cmdQueue.Enqueue(commandToSend);
        }

        public void PowerOn()
        {
            _logger.PrintLine("Trying to send power on...");
            lock (_mainLock)
            {
                if (IsPoweredOn)
                {
                    _logger.PrintLine("Already powered on");
                    return;
                }

                WakeOnLanUtility.SendWol(_ipAddress, _macAddress, 1);
                CrestronEnvironment.Sleep(10);
                WakeOnLanUtility.SendWol(_ipAddress, _macAddress, 1);
            }
            _logger.PrintLine("Sent power on");
        }

        public void PowerOff()
        {
            _logger.PrintLine("Trying to send power off...");
            lock (_mainLock)
            {
                if (!IsPoweredOn)
                {
                    _logger.PrintLine("Already powered off");
                    return;
                }

                SendCommand(new Command(CommandPriorities.Highest, "{\"type\":\"request\",\"id\":\"powerOff\",\"uri\":\"ssap://system/turnOff\"}"));
                ResetHeartbeat(5000);
            }

            _logger.PrintLine("Sent power off");
        }

        public void SetVolume(ushort value)
        {
            if (!IsPoweredOn)
                return;

            var volume = ScaleDown(value);

            SendCommand(new Command(CommandPriorities.Medium, "{\"type\":\"request\",\"id\":\"setVolume\",\"uri\":\"ssap://audio/setVolume\",\"payload\":{\"volume\":" + volume + "}}"));
        }

        public void IncrementVolume()
        {
            if (!IsPoweredOn)
                return;

            SendCommand(new Command(CommandPriorities.Medium, "{\"type\":\"request\",\"id\":\"volumeUp\",\"uri\":\"ssap://audio/volumeUp\"}"));
        }

        public void DecrementVolume()
        {
            if (!IsPoweredOn)
                return;

            SendCommand(new Command(CommandPriorities.Medium, "{\"type\":\"request\",\"id\":\"volumeDown\",\"uri\":\"ssap://audio/volumeDown\"}"));
        }

        public void SetMute(ushort value)
        {
            if (!IsPoweredOn)
                return;

            SendCommand(new Command(CommandPriorities.Medium, string.Format("{\"type\":\"request\",\"id\":\"volumeMuteOn\",\"uri\":\"ssap://audio/setMute\", \"payload\":{\"mute\": {0}}}", Convert.ToBoolean(value))));
        }

        public void SendKey(string name)
        {
            if (!IsPoweredOn)
                return;

            if (_inputControls != null)
            {
                if (_inputControls.IsConnected)
                {
                    _inputControls.SendKey(name);
                }
                else
                {
                    SendCommand(new Command(CommandPriorities.Medium, "{\"type\":\"request\",\"id\":\"getInputSocket\",\"uri\":\"ssap://com.webos.service.networkinput/getPointerInputSocket\"}"));
                }
            }
            else
            {
                SendCommand(new Command(CommandPriorities.Medium, "{\"type\":\"request\",\"id\":\"getInputSocket\",\"uri\":\"ssap://com.webos.service.networkinput/getPointerInputSocket\"}"));
            }
        }

        public void ChangeInput(ushort input)
        {
            if (!IsPoweredOn)
                return;

            if (_externalInputs != null)
            {
                if (_externalInputs.Count < input)
                    return;

                SendCommand(new Command(CommandPriorities.Medium, "{\"type\":\"request\",\"id\":\"changeInput_" + _externalInputs[input - 1].Id + "\",\"uri\":\"ssap://tv/switchInput\", \"payload\":{\"inputId\": \"" + _externalInputs[input - 1].Id + "\"}}"));
            }
            else
            {
                GetInputs();
            }
        }

        public void GetInputs()
        {
            if (!IsPoweredOn)
                return;

            SendCommand(new Command(CommandPriorities.Medium, "{\"type\":\"request\",\"id\":\"getExternalInputs\",\"uri\":\"ssap://tv/getExternalInputList\"}"));
        }

        public void LaunchApp(ushort index)
        {
            if (!IsPoweredOn)
                return;

            if (_apps != null)
            {
                if (_apps.Count < index)
                    return;

                SendCommand(new Command(CommandPriorities.Medium, "{\"type\":\"request\",\"id\":\"launchApp\",\"uri\":\"ssap://com.webos.applicationManager/launch\", \"payload\": {\"id\": \"" + _apps[index - 1].Id + "\"}}"));
            }
            else
            {
                GetApps();
            }
        }

        public void GetApps()
        {
            if (!IsPoweredOn)
                return;

            SendCommand(new Command(CommandPriorities.Medium, "{\"type\":\"request\",\"id\":\"getAllApps\",\"uri\":\"ssap://com.webos.applicationManager/listLaunchPoints\"}"));
        }

        public void SendNotification(string value)
        {
            if (!IsPoweredOn)
                return;

            SendCommand(new Command(CommandPriorities.Low, "{\"type\":\"request\",\"id\":\"sendNotification\",\"uri\":\"ssap://system.notifications/createToast\",\"payload\":{\"message\":\"" + value + "\"}}"));
        }
        #endregion

        #region Timers
        private void DisplayGetInfo()
        {
            if (!IsPoweredOn)
                return;

            SendCommand(new Command(CommandPriorities.Highest, "{\"type\":\"request\",\"id\":\"getInputSocket\",\"uri\":\"ssap://com.webos.service.networkinput/getPointerInputSocket\"}"));
            GetApps();
            GetInputs();
        }
        #endregion

        #region Method Helpers
        private static int ScaleUp(int level)
        {
            var levelScaled = (level * (65535.0 /100));
            var rounded = Math.Round(levelScaled);
            return Convert.ToInt32(rounded);
        }

        private static int ScaleDown(int level)
        {
            var levelScaled = (level / (65535.0 / 100.0));
            var rounded = Math.Round(levelScaled);
            return Convert.ToInt32(rounded);
        }
        #endregion

        private void OnUShortEvent(UShortEventHandler handler, UShortEventArgs e)
        {
            var h = handler;

            if (h == null) return;
            h.Invoke(this, e);
        }

        private void OnStringEvent(StringEventHandler handler, StringEventArgs e)
        {
            var h = handler;

            if (h == null) return;
            h.Invoke(this, e);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        private void Dispose(bool disposing)
        {
            if (Disposed) return;

            Disposed = true;

            if (disposing)
            {
                _heartbeatTimer.Stop();
                _heartbeatFailedTimer.Stop();
                _heartbeatTimer.Dispose();
                _heartbeatFailedTimer.Dispose();

                _cmdQueueDequeuer.Stop();
                _cmdQueueDequeuer.Dispose();
                _cmdQueue.Clear();
                
                if(_inputControls != null) _inputControls.Dispose();
                _wsClient.Dispose();
            }
        }
    }
}
