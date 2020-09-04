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
        public class MessageSchema : Chetch.Messaging.MessageSchema
        {
            public MessageSchema() { }

            public MessageSchema(Message message) : base(message) { }

            public void AddAlarms(List<Chetch.Database.DBRow> rows)
            {
                Message.AddValue("Alarms", rows.Select(i => i.GenerateParamString()).ToList());
            }

            public List<String> GetAlarms()
            {
                return Message.GetList<String>("Alarms");
            }

            public void AddAlarmStates(Dictionary<String, AlarmState> states)
            {
                Message.AddValue("AlarmStates", states);
                bool on = false;
                foreach (var astate in states.Values)
                {
                    if (astate == AlarmState.ON)
                    {
                        on = true;
                        break;
                    }
                }
            }

            public Dictionary<String, AlarmState> GetAlarmStates()
            {
                return Message.HasValue("AlarmStates") ? Message.GetDictionary<AlarmState>("AlarmStates") : null;
            }

            public void AddBuzzer(Buzzer buzzer)
            {
                Message.AddValue("Buzzer", buzzer.ToString());
                Message.AddValue("Silenced", buzzer.IsSilenced);
            }

            public Message AlertAlarmsService(String deviceID, AlarmState alarmState, bool testing = false)
            {
                Message msg = new Message(MessageType.ALERT);
                msg.AddValue("DeviceID", deviceID);
                msg.AddValue("AlarmState", alarmState);
                msg.AddValue("Testing", testing);
                return msg;
            }

            public bool IsAlert()
            {
                return Message.HasValue("AlarmState");
            }

            public AlarmState GetAlarmState()
            {
                return Message.GetEnum<AlarmState>("AlarmState");
            }

            public void AddTesting(bool testing)
            {
                Message.AddValue("Testing", testing);
            }

            public bool IsTesting()
            {
                return Message.GetBool("Testing");
            }
        }

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

            public AlarmState AlarmState { get; internal set; } = AlarmState.OFF;
            public bool IsOn { get { return AlarmState == AlarmState.ON; } }
            public bool IsOff { get { return !IsOn; } }
            public bool Enabled { get; internal set; } = true;

            private MessageSchema _schema = new MessageSchema();

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
                _schema.Message = message;
                if (_schema.IsAlert())
                {
                    AlarmState = _schema.GetAlarmState();
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

        private String _testingAlarmID = null;
        private System.Timers.Timer _testAlarmTimer = null;

        public bool IsTesting { get { return _testingAlarmID != null; } }

        public BBAlarmsService() : base("BBAlarms", "BBAlarmsClient", "BBAlarmsService", "BBAlarmsServiceLog")
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
                _monitorRemoteAlarmsTimer.Interval = 30 * 1000;
                _monitorRemoteAlarmsTimer.Elapsed += new System.Timers.ElapsedEventHandler(MonitorRemoteAlarms);

                _testAlarmTimer = new System.Timers.Timer();
                _testAlarmTimer.Interval = 5 * 1000;
                _testAlarmTimer.Elapsed += new System.Timers.ElapsedEventHandler(EndAlarmTest);
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
            //Start timer to monitor remote alarms to ensure synchronised
            _monitorRemoteAlarmsTimer.Start();
        }

        public override void AddCommandHelp(List<string> commandHelp)
        {
            base.AddCommandHelp(commandHelp);

            commandHelp.Add("list-alarms:  Lists active alarms in the alarms database");
            commandHelp.Add("alarm-status:  Lists alarms currently on and currently off");
            commandHelp.Add("silence: Turn buzzer off for <seconds>");
            commandHelp.Add("unsilence: Unsilence buzzer");
            commandHelp.Add("disable-alarm: Set <alarm> to State DISABLED");
            commandHelp.Add("enable-alarm: Set <alarm> to state ENABLED");
            commandHelp.Add("test-alarm: Set <alarm> to ON for a short period of time");
        }

        private bool HasAlarmWithState(AlarmState alarmState, Dictionary<String, AlarmState> states = null)
        {
            if (states == null) states = _alarmStates;

            foreach (var state in states.Values)
            {
                if (state == alarmState) return true;
            }
            return false;
        }

        private bool IsAlarmOn(Dictionary<String, AlarmState> states = null)
        {
            return HasAlarmWithState(AlarmState.ON, states);
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
            OnAlarmStateChanged(a.DeviceID, a.AlarmState);
        }

        private void OnAlarmStateChanged(String deviceID, AlarmState newState, String comments = null)
        {
            //if this is called while testing then we end the test as his takes priority
            if (IsTesting)
            {
                EndAlarmTest(String.Format("Ending because {0} changed state to {1}", deviceID, newState), null);
            }

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
            if (IsAlarmOn())
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
            MessageSchema schema = new MessageSchema(response);
            switch (cmd)
            {
                case "list-alarms":
                    var rows = _asdb.SelectDevices();
                    schema.AddAlarms(rows);
                    return true;

                case "alarm-status":
                    schema.AddAlarmStates(_alarmStates);
                    schema.AddBuzzer(_buzzer);
                    schema.AddTesting(IsTesting);
                    return true;

                case "silence":
                    if (ADMS.Count == 0) throw new Exception("No boards connected");
                    int secs = args.Count > 0 ? System.Convert.ToInt16(args[0]) : 60 * 5;
                    if (IsAlarmOn() && secs > 0)
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

                case "test-alarm":
                    if (args.Count == 0) throw new Exception("No alarm specified to test");
                    id = args[0].ToString();
                    if (!_alarmStates.ContainsKey(id)) throw new Exception(String.Format("No alarm found with id {0}", id));
                    StartAlarmTest(id);
                    response.Value = String.Format("Testing alarm {0}", id);
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
                    var schema = new MessageSchema(message);
                    var remoteStates = schema.GetAlarmStates();
                    if (remoteStates != null)
                    {
                        foreach (var kv in remoteStates)
                        {
                            if (_alarmStates.ContainsKey(kv.Key) && _alarmStates[kv.Key] != kv.Value)
                            {
                                //A remote alarm has a different state from the one that we have recorded in this service so we
                                //take the remote state as authority and upcate accordingly
                                String msg = String.Format("Alarm {0} has state {1} but remote state {2} so updating locally", kv.Key, _alarmStates[kv.Key], kv.Value);
                                Tracing?.TraceEvent(TraceEventType.Warning, 2000, msg);
                                OnAlarmStateChanged(kv.Key, kv.Value, msg);
                            }
                        }
                    }

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

        //testing
        private void StartAlarmTest(String deviceID)
        {
            if (_testingAlarmID != null) throw new Exception(String.Format("Cannot test alarm {0} as already testing {1}", deviceID, _testingAlarmID));
            if (!_alarmStates.ContainsKey(deviceID)) throw new Exception(String.Format("No alarm found with id {0}", deviceID));
            if (IsAlarmOn()) throw new Exception("Cannot test any alarm while an alarm is already on");
            if (_alarmStates[deviceID] != AlarmState.OFF && _alarmStates[deviceID] != AlarmState.ENABLED) throw new Exception(String.Format("Cannot test alarm {0} as it is {1}", deviceID, _alarmStates[deviceID]));

            OnAlarmStateChanged(deviceID, AlarmState.ON, "Start alarm test");

            //note: these have to be placed after call to state change (see OnStateChange method)
            _testingAlarmID = deviceID;
            _testAlarmTimer.Start();
        }

        private void EndAlarmTest(Object sender, System.Timers.ElapsedEventArgs ea)
        {
            _testAlarmTimer.Stop();
            var deviceID = _testingAlarmID;
            _testingAlarmID = null;

            String logMsg;
            if (sender is String)
            {
                logMsg = sender.ToString();
            }
            else
            {
                logMsg = "End alarm test after timeout";
            }
            OnAlarmStateChanged(deviceID, AlarmState.OFF, logMsg);
        }
    } //end class
}