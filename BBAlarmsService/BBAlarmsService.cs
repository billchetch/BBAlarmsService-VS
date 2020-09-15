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
    public class BBAlarmsService : ADMService
    {
        new public class MessageSchema : Chetch.Messaging.MessageSchema
        {
            public const String COMMAND_ALARM_STATUS = "alarm-status";
            public const String COMMAND_LIST_ALARMS = "list-alarms";
            public const String COMMAND_SILENCE = "silence";
            public const String COMMAND_UNSILENCE = "unsilence";
            public const String COMMAND_DISABLE_ALARM = "disable-alarm";
            public const String COMMAND_ENABLE_ALARM = "enable-alarm";
            public const String COMMAND_TEST_ALARM = "test-alarm";
            
            static public Message AlertAlarmStateChange(String deviceID, AlarmState alarmState, Buzzer buzzer, Chetch.Arduino.Devices.Switch pilot, bool testing = false)
            {
                Message msg = new Message(MessageType.ALERT);
                msg.AddValue("DeviceID", deviceID);
                msg.AddValue("AlarmState", alarmState);
                msg.AddValue("Testing", testing);

                var schema = new MessageSchema(msg);
                schema.AddBuzzer(buzzer);
                schema.AddPilot(pilot);
                return msg;
            }

            public MessageSchema() { }

            public MessageSchema(Message message) : base(message) { }

            public void AddAlarms(List<Chetch.Database.DBRow> rows)
            {
                Message.AddValue("Alarms", rows.Select(i => i.GenerateParamString(true)).ToList());
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
                if (buzzer == null) return;

                Message.AddValue("Buzzer", buzzer.ToString());
                Message.AddValue("BuzzerID", buzzer.ID);
                Message.AddValue("BuzzerOn", buzzer.IsOn);
                Message.AddValue("BuzzerSilenced", buzzer.IsSilenced);
            }

            public void AddPilot(Chetch.Arduino.Devices.Switch pilot)
            {
                if (pilot == null) return;

                Message.AddValue("Pilot", pilot.ToString());
                Message.AddValue("PilotID", pilot.ID);
                Message.AddValue("PilotOn", pilot.IsOn);
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
            OFF,
            ON,
            DISABLED
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

        public BBAlarmsService() :base("BBAlarms", "BBAlarmsClient", "BBAlarmsService", "BBAlarmsServiceLog") // base("BBAlarms", "ADMTestServiceClient", "ADMTestService", "ADMTestServiceLog")
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

            //TODO: uncomment monitor start
            _monitorRemoteAlarmsTimer.Start();
        }

        public override void AddCommandHelp()
        {
            base.AddCommandHelp();

            AddCommandHelp(MessageSchema.COMMAND_LIST_ALARMS, "Lists active alarms in the alarms database");
            AddCommandHelp(MessageSchema.COMMAND_ALARM_STATUS, "Lists state of alarms and some other stuff");
            AddCommandHelp(MessageSchema.COMMAND_SILENCE, "Turn buzzer off for <seconds>");
            AddCommandHelp(MessageSchema.COMMAND_UNSILENCE,  "Unsilence buzzer");
            AddCommandHelp(MessageSchema.COMMAND_DISABLE_ALARM,"Set <alarm> to State DISABLED");
            AddCommandHelp(MessageSchema.COMMAND_ENABLE_ALARM, "Set <alarm> to state ENABLED");
            AddCommandHelp(MessageSchema.COMMAND_TEST_ALARM, "Set <alarm> to ON for a short period of time");
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
            ArduinoDevice dev;
            switch (message.Type)
            {
                case MessageType.DATA:
                    dev = adm.GetDevice(message.Sender);
                    if (dev != null && _alarmStates.ContainsKey(dev.ID) && message.HasValue("State"))
                    {
                        bool newState = message.GetBool("State");
                        OnAlarmStateChanged(dev.ID, newState ? AlarmState.ON : AlarmState.OFF);
                        if (message.Tag == 0) return; //i.e. hasn't been specifically requested so do not call base method as this will broadcast (which is not necessary because OnAlarmStateChanged broadcasts)
                    }
                    break;

                case MessageType.NOTIFICATION:
                    dev = adm.GetDevice(message.Sender);
                    if (dev.ID.Equals(_buzzer.ID))
                    {
                        var schema = new MessageSchema(message);
                        schema.AddBuzzer(_buzzer);
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

        private void OnAlarmStateChanged(String deviceID, AlarmState newState, String comments = null, bool testing = false)
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

            //finally we broadcast to any listeners
            var alert = MessageSchema.AlertAlarmStateChange(deviceID, newState, _buzzer, _pilot, testing);
            Broadcast(alert);
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
                case MessageSchema.COMMAND_LIST_ALARMS:
                    var rows = _asdb.SelectDevices();
                    schema.AddAlarms(rows);
                    return true;

                case MessageSchema.COMMAND_ALARM_STATUS:
                    schema.AddAlarmStates(_alarmStates);
                    schema.AddBuzzer(_buzzer);
                    schema.AddPilot(_pilot);
                    schema.AddTesting(IsTesting);
                    return true;

                case MessageSchema.COMMAND_SILENCE:
                    if (ADMS.Count == 0) throw new Exception("No boards connected");
                    int secs = args.Count > 0 ? System.Convert.ToInt16(args[0]) : 60 * 5;
                    if (IsAlarmOn() && !_buzzer.IsSilenced && secs > 0)
                    {
                        _buzzer.Silence(secs * 1000);
                        schema.AddBuzzer(_buzzer);
                        message.Value = String.Format("Buzzer silenced for {0} seconds", secs);
                        return true;
                    }
                    else
                    {
                        //don't send a messages
                        return false;
                    }

                case MessageSchema.COMMAND_UNSILENCE:
                    if (ADMS.Count == 0) throw new Exception("No boards connected");
                    _buzzer.Unsilence();
                    schema.AddBuzzer(_buzzer);
                    message.Value = "Buzzer unsilenced";
                    return true;

                case MessageSchema.COMMAND_DISABLE_ALARM:
                    if (args.Count == 0) throw new Exception("No alarm specified to disable");
                    id = args[0].ToString();
                    if (!_alarmStates.ContainsKey(id)) throw new Exception(String.Format("No alarm found with id {0}", id));
                    EnableAlarm(id, false);
                    OnAlarmStateChanged(id, AlarmState.DISABLED, String.Format("Command sent from {0}", message.Sender));
                    response.Value = String.Format("Alarm {0} disabled", id);
                    return true;

                case MessageSchema.COMMAND_ENABLE_ALARM:
                    if (args.Count == 0) throw new Exception("No alarm specified to enable");
                    id = args[0].ToString();
                    if (!_alarmStates.ContainsKey(id)) throw new Exception(String.Format("No alarm found with id {0}", id));
                    EnableAlarm(id, true);
                    OnAlarmStateChanged(id, AlarmState.OFF, String.Format("Command sent from {0}", message.Sender));
                    response.Value = String.Format("Alarm {0} enabled", id);
                    return true;

                case MessageSchema.COMMAND_TEST_ALARM: 
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
                SendCommand(client, MessageSchema.COMMAND_ALARM_STATUS);
            }
        }

        //testing
        private void StartAlarmTest(String deviceID)
        {
            if (IsTesting) throw new Exception(String.Format("Cannot test alarm {0} as already testing {1}", deviceID, _testingAlarmID));
            if (!_alarmStates.ContainsKey(deviceID)) throw new Exception(String.Format("No alarm found with id {0}", deviceID));
            if (IsAlarmOn()) throw new Exception("Cannot test any alarm while an alarm is already on");
            if (_alarmStates[deviceID] != AlarmState.OFF) throw new Exception(String.Format("Cannot test alarm {0} as it is {1}", deviceID, _alarmStates[deviceID]));

            OnAlarmStateChanged(deviceID, AlarmState.ON, "Start alarm test", true);

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
            OnAlarmStateChanged(deviceID, AlarmState.OFF, logMsg, true);
        }
    } //end class
}