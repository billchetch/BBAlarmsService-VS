using System;
using System.Collections.Generic;
using System.Linq;
using Chetch.Messaging;
using System.Diagnostics;
using Chetch.Arduino;
using Chetch.Arduino.Devices;
using Chetch.Arduino.Devices.Buzzers;

namespace BBAlarmsService
{
    class BBAlarmsService : ADMService
    {
        public enum AlarmState
        {
            ON,
            OFF,
            DISABLED,
            ENABLED
        }

        class RemoteAlarm : ADMMessageFilter
        {
            public String AlarmName { get; internal set; }

            public bool State { get; internal set; } = false;
            public bool IsOn { get { return State; } }
            public bool IsOff { get { return !State; } }
            public bool Enabled { get; internal set; } = true;


            public RemoteAlarm(String deviceID, String alarmName, String clientName, Action<MessageFilter, Message> onMatched) : base(deviceID, clientName, Chetch.Messaging.MessageType.ALERT, onMatched)
            {
                AlarmName = alarmName;
            }

            public void Enable(bool enabled = true)
            {
                Enabled = enabled;
            }

            protected override void OnMatched(Message message)
            {
                if (!Enabled) return;

                if (message.HasValue("State"))
                {
                    State = message.GetBool("State");
                }

                base.OnMatched(message);
            }
        }


        public const int PILOT_LIGHT_PIN = 3;
        public const int BUZZER_PIN = 4;
        private AlarmsServiceDB _asdb;

        private List<SwitchSensor> _localAlarms = new List<SwitchSensor>();
        private List<RemoteAlarm> _remoteAlarms = new List<RemoteAlarm>();
        private Dictionary<String, AlarmState> _alarmStates = new Dictionary<String, AlarmState>();

        private Chetch.Arduino.Devices.Switch _pilot;
        private Buzzer _buzzer;

        private System.Timers.Timer _monitorRemoteAlarmsTimer = null;
        private List<String> _remoteClients = new List<String>();

        public BBAlarmsService() : base("BBAlarms", "ADMTestServiceClient", "ADMTestService", "ADMTestServiceLog") //base("BBAlarms", "BBAlarmsClient", "BBAlarmsService", "BBAlarmsServiceLog")
        {
            SupportedBoards = ArduinoDeviceManager.DEFAULT_BOARD_SET;
            AllowedPorts = Properties.Settings.Default.AllowedPorts;
            try
            {
                Tracing?.TraceEvent(TraceEventType.Information, 0, "Connecting to Alarms database...");
                _asdb = AlarmsServiceDB.Create(Properties.Settings.Default, "AlarmsDBName");
                Tracing?.TraceEvent(TraceEventType.Information, 0, "Connected to Alarms database. Now creating alarms...");

                var rows = _asdb.SelectDevices();
                foreach (var row in rows)
                {
                    String source = row.GetString("alarm_source");
                    String deviceID = row.GetString("device_id");
                    if (source == null || source == String.Empty)
                    {
                        //The alarm is local and provided by an ADM input
                        int pin = row.GetInt("pin_number");
                        if (pin == 0) throw new Exception("BBAlarmsService: Cannot have an alarm pin 0");
                        SwitchSensor la = new SwitchSensor(pin, row.GetInt("noise_threshold"), deviceID, row.GetString("device_name"));
                        _localAlarms.Add(la);
                        Tracing?.TraceEvent(TraceEventType.Information, 0, "Created {0} local alarm with id {1} and name {2} for pin {3}", row["alarm_name"], la.ID, la.Name, pin);
                    }
                    else
                    {
                        //The alarm is remote so we need to subscribe to the remote service and listen
                        RemoteAlarm ra = new RemoteAlarm(deviceID, row.GetString("alarm_name"), source, HandleRemoteAlarmMessage);
                        _remoteAlarms.Add(ra);
                        Subscribe(ra);
                        Tracing?.TraceEvent(TraceEventType.Information, 0, "Created {0} alarm @ {1} with id {2}", ra.AlarmName, source, ra.DeviceID);

                        if (!_remoteClients.Contains(source)) _remoteClients.Add(source);
                    }
                    _alarmStates[deviceID] = AlarmState.OFF;
                }

                _monitorRemoteAlarmsTimer = new System.Timers.Timer();
                _monitorRemoteAlarmsTimer.Stop();
                _monitorRemoteAlarmsTimer.Interval = 30 * 1000;
                _monitorRemoteAlarmsTimer.Elapsed += new System.Timers.ElapsedEventHandler(MonitorRemoteAlarms);
            }
            catch (Exception e)
            {
                Tracing?.TraceEvent(TraceEventType.Error, 0, e.Message);
                throw e;
            }
        }

        protected override void AddADMDevices(ArduinoDeviceManager adm, ADMMessage message)
        {
            _pilot = new Chetch.Arduino.Devices.Switch(PILOT_LIGHT_PIN);
            adm.AddDevice(_pilot);

            _buzzer = new Buzzer(BUZZER_PIN);
            adm.AddDevice(_buzzer);

            foreach (var a in _localAlarms)
            {
                Tracing?.TraceEvent(TraceEventType.Information, 0, "Adding alarm {0} {1} to {2}", a.ID, a.Name, adm.BoardID);
                adm.AddDevice(a);
            }
        }

        protected override void OnClientConnect(ClientConnection cnn)
        {
            base.OnClientConnect(cnn);

            _monitorRemoteAlarmsTimer.Start();
        }

        public override void AddCommandHelp(List<string> commandHelp)
        {
            base.AddCommandHelp(commandHelp);

            commandHelp.Add("list-alarms:  Lists active alarms in the alarms database");
            commandHelp.Add("alarm-status:  Lists alarms currently on and currently off");
            commandHelp.Add("silence: Turn buzzer off for <seconds>");
            commandHelp.Add("unsilence: Unsilence the buzzer");
        }

        private bool IsAlarmOn
        {
            get
            {
                bool localAlarmOn = false;
                if (ADMS.Count > 0)
                {
                    var adm = GetADM(null);
                    if (adm != null && adm.IsConnected)
                    {
                        foreach (var a in _localAlarms)
                        {
                            if (a.IsConnected && a.IsOn)
                            {
                                localAlarmOn = true;
                                break;
                            }
                        }
                    }
                }

                bool remoteAlarmOn = false;
                foreach (var ra in _remoteAlarms)
                {
                    if (ra.IsOn)
                    {
                        remoteAlarmOn = true;
                        break;
                    }
                }

                return localAlarmOn || remoteAlarmOn;
            }
        }

        protected override void HandleADMMessage(ADMMessage message, ArduinoDeviceManager adm)
        {
            switch (message.Type)
            {
                case MessageType.DATA:
                    var dev = adm.GetDevice(message.Sender);
                    if (dev != null && message.HasValue("State"))
                    {
                        bool newState = message.GetBool("State");
                        OnAlarmStateChanged(dev.ID, newState ? AlarmState.ON : AlarmState.OFF);
                    }
                    break;
            }

            base.HandleADMMessage(message, adm);
        }

        private void HandleRemoteAlarmMessage(MessageFilter remote, Message message)
        {
            RemoteAlarm a = (RemoteAlarm)remote;
            OnAlarmStateChanged(a.DeviceID, a.State ? AlarmState.ON : AlarmState.OFF);
        }

        private void OnAlarmStateChanged(String deviceID, AlarmState newState, String comments = null)
        {
            //keep track of the new state in a ID to state map
            _alarmStates[deviceID] = newState;

            //a state change has occurred so we log it
            try
            {
                Tracing?.TraceEvent(TraceEventType.Information, 0, "Logging alarm device {0} change of state to {1}", deviceID, newState);
                _asdb.LogStateChange(deviceID, newState.ToString(), comments);
            }
            catch (Exception e)
            {
                Tracing?.TraceEvent(TraceEventType.Error, 0, "Logging alarm error: {0}", e.Message);
            }

            //turn buzzer on or off
            if (IsAlarmOn)
            {
                _pilot.On();
                _buzzer.On();
            }
            else
            {
                _pilot.Off();
                _buzzer.Off();
            }
        }

        private void EnableAlarm(String id, bool enable)
        {
            //search local alarms
            foreach (var a in _localAlarms)
            {
                if (a.ID.Equals(id))
                {
                    a.Enable(enable);
                    return;
                }
            }

            //search remote alarms
            foreach (var a in _remoteAlarms)
            {
                if (a.DeviceID.Equals(id))
                {
                    a.Enable(enable);
                    return;
                }
            }
        }

        override public bool HandleCommand(Connection cnn, Message message, String cmd, List<Object> args, Message response)
        {
            String id; //used for alarm id
            switch (cmd)
            {
                case "list-alarms":
                    var rows = _asdb.SelectDevices();
                    response.AddValue("AlarmDBIDs", rows.Select(i => i.ID));
                    response.AddValue("AlarmDeviceIDs", rows.Select(i => i["device_id"]));
                    response.AddValue("AlarmNames", rows.Select(i => i["alarm_name"]));
                    response.AddValue("AlarmSources", rows.Select(i => i.IsNull("alarm_source") ? "local" : i.GetString("alarm_source")));
                    response.AddValue("AlarmPins", rows.Select(i => i.GetInt("pin_number", 0)));
                    return true;

                case "alarm-status":
                    List<String> alarmsOn = new List<String>();
                    List<String> alarmsOff = new List<String>();
                    List<String> alarmsDisabled = new List<String>();
                    foreach (var kv in _alarmStates)
                    {
                        switch (kv.Value)
                        {
                            case AlarmState.ON:
                                alarmsOn.Add(kv.Key); break;
                            case AlarmState.OFF:
                                alarmsOff.Add(kv.Key); break;
                            case AlarmState.DISABLED:
                                alarmsDisabled.Add(kv.Key); break;
                        }
                    }
                    response.AddValue("AlarmsOn", alarmsOn);
                    response.AddValue("AlarmsOff", alarmsOff);
                    response.AddValue("AlarmsDisabled", alarmsDisabled);
                    response.AddValue("AlarmOn", alarmsOn.Count > 0);
                    response.AddValue("Silenced", _buzzer.IsSilenced);
                    return true;

                case "silence":
                    if (ADMS.Count == 0) throw new Exception("No boards connected");
                    int secs = args.Count > 0 ? System.Convert.ToInt16(args[0]) : 60 * 5;
                    if (IsAlarmOn && secs > 0)
                    {
                        _buzzer.Silence(secs * 1000);
                        message.Value = String.Format("Buzzer silenced for {0} seconds", secs);
                        return true;
                    }
                    else
                    {
                        //don't send a messages
                        return false;
                    }

                case "unsilence":
                    if (ADMS.Count == 0) throw new Exception("No boards connected");
                    _buzzer.Unsilence();
                    message.Value = "Buzzer unsilenced";
                    return true;

                case "disable-alarm":
                    if (args.Count == 0) throw new Exception("No alarm specified to disable");
                    id = args[0].ToString();
                    if (!_alarmStates.ContainsKey(id)) throw new Exception(String.Format("No alarm found with id {0}", id));
                    var adm = GetADM(null);
                    EnableAlarm(id, false);
                    OnAlarmStateChanged(id, AlarmState.DISABLED, String.Format("Command sent from {0}", message.Sender));
                    response.Value = String.Format("Alarm {0} disabled", id);
                    return true;

                case "enable-alarm":
                    if (args.Count == 0) throw new Exception("No alarm specified to enable");
                    id = args[0].ToString();
                    if (!_alarmStates.ContainsKey(id)) throw new Exception(String.Format("No alarm found with id {0}", id));
                    EnableAlarm(id, true);
                    OnAlarmStateChanged(id, AlarmState.ENABLED, String.Format("Command sent from {0}", message.Sender));
                    response.Value = String.Format("Alarm {0} enabled", id);
                    return true;


                default:
                    return base.HandleCommand(cnn, message, cmd, args, response);
            }
        }

        public override void HandleClientMessage(Connection cnn, Message message)
        {
            switch (message.Type)
            {
                case MessageType.COMMAND_RESPONSE:
                    //message.Sender;
                    break;
            }
            base.HandleClientMessage(cnn, message);
        }

        private void MonitorRemoteAlarms(Object sender, System.Timers.ElapsedEventArgs ea)
        {

            Message message = new Message(Chetch.Messaging.MessageType.COMMAND);

            foreach (var client in _remoteClients)
            {
                SendCommand(client, "alarm-status");
            }
        }
    } //end class
}