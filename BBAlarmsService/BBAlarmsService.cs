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
        class LocalAlarm
        {
            public String AlarmName { get; internal set; }
            public SwitchSensor AlarmSwitch { get; internal set; }
            
            public LocalAlarm(String alarmName, SwitchSensor alarmSwitch)
            {
                AlarmName = alarmName;
                AlarmSwitch = alarmSwitch;
            }
        }

        class RemoteAlarm : ArduinoDeviceMessageFilter
        {
            public String AlarmName { get; internal set; }
            public AlarmState AlarmState { get; internal set; } = AlarmState.OFF;
            public String AlarmMessage { get; internal set; }
            public bool IsOn { get { return AlarmsMessageSchema.IsAlarmStateOn(AlarmState); } }
            public bool IsOff { get { return !IsOn; } }
            public bool Enabled { get; internal set; } = true;

            private AlarmsMessageSchema _schema = new AlarmsMessageSchema();

            public RemoteAlarm(String deviceID, String alarmName, String clientName) : base(deviceID, clientName, Chetch.Messaging.MessageType.ALERT)
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
                AlarmState = _schema.GetAlarmState();
                AlarmMessage = _schema.GetAlarmMessage();
                
                base.OnMatched(message);
            }
        }

        public const int PILOT_LIGHT_PIN = 3;
        public const int BUZZER_PIN = 4;
        private AlarmsServiceDB _asdb;

        private List<LocalAlarm> _localAlarms = new List<LocalAlarm>();
        private List<RemoteAlarm> _remoteAlarms = new List<RemoteAlarm>();
        private Dictionary<String, AlarmState> _alarmStates = new Dictionary<String, AlarmState>();
        private Dictionary<String, String> _alarmMessages = new Dictionary<String, String>();

        private Chetch.Arduino.Devices.Switch _pilot;
        private Buzzer _buzzer;

        private System.Timers.Timer _updateAlarmStatesTimer = null; //for remote alarms and also broadcast to clients
        private List<String> _remoteClients = new List<String>();

        private String _testingAlarmID = null;
        private System.Timers.Timer _testAlarmTimer = null;

        public bool IsTesting { get { return _testingAlarmID != null; } }

        public BBAlarmsService() : base("BBAlarms", "BBAlarmsClient", "BBAlarmsService", "BBAlarmsServiceLog") //  base("BBAlarms", "ADMTestServiceClient", "ADMTestService", "ADMTestServiceLog") /
        {
            SupportedBoards = ArduinoDeviceManager.DEFAULT_BOARD_SET;
            RequiredBoards = "ALM1";
            AddAllowedPorts(Properties.Settings.Default.AllowedPorts);
            MaxPingResponseTime = 20;
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
                    String alarmName = row.GetString("alarm_name");
                    if (source == null || source == String.Empty)
                    {
                        //The alarm is local and provided by an ADM input
                        int pin = row.GetInt("pin_number");
                        if (pin == 0) throw new Exception("BBAlarmsService: Cannot have an alarm pin 0");
                        SwitchSensor ss = new SwitchSensor(pin, row.GetInt("noise_threshold"), deviceID, row.GetString("device_name"));
                        LocalAlarm la = new LocalAlarm(alarmName, ss);
                        _localAlarms.Add(la);
                        Tracing?.TraceEvent(TraceEventType.Information, 0, "Created {0} local alarm with device id {1} and device name {2} for pin {3}", row["alarm_name"], ss.ID, ss.Name, pin);
                    }
                    else
                    {
                        //The alarm is remote so we need to subscribe to the remote service and listen
                        RemoteAlarm ra = new RemoteAlarm(deviceID, alarmName, source);
                        ra.HandleMatched += HandleRemoteAlarmMessage;
                        _remoteAlarms.Add(ra);
                        Subscribe(ra);
                        Tracing?.TraceEvent(TraceEventType.Information, 0, "Created {0} alarm @ {1} with id {2}", ra.AlarmName, source, deviceID);

                        if (!_remoteClients.Contains(source)) _remoteClients.Add(source);
                    }
                    _alarmStates[deviceID] = AlarmState.OFF;
                    _alarmMessages[deviceID] = null;
                }

                _updateAlarmStatesTimer = new System.Timers.Timer();
                _updateAlarmStatesTimer.Interval = 30 * 1000;
                _updateAlarmStatesTimer.Elapsed += new System.Timers.ElapsedEventHandler(UpdateAlarmStates);

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
                Tracing?.TraceEvent(TraceEventType.Information, 0, "Adding alarm {0} {1} to {2}", a.AlarmSwitch.ID, a.AlarmName, adm.BoardID);
                adm.AddDevice(a.AlarmSwitch);
            }
        }

        protected override void OnClientConnect(ClientConnection cnn)
        {
            base.OnClientConnect(cnn);

            //TODO: uncomment monitor start
            _updateAlarmStatesTimer.Start();
        }

        public override void AddCommandHelp()
        {
            base.AddCommandHelp();

            AddCommandHelp(AlarmsMessageSchema.COMMAND_LIST_ALARMS, "Lists active alarms in the alarms database");
            AddCommandHelp(AlarmsMessageSchema.COMMAND_ALARM_STATUS, "Lists state of alarms and some other stuff");
            AddCommandHelp(AlarmsMessageSchema.COMMAND_SILENCE, "Turn buzzer off for <seconds>");
            AddCommandHelp(AlarmsMessageSchema.COMMAND_UNSILENCE, "Unsilence buzzer");
            AddCommandHelp(AlarmsMessageSchema.COMMAND_DISABLE_ALARM, "Set <alarm> to State DISABLED");
            AddCommandHelp(AlarmsMessageSchema.COMMAND_ENABLE_ALARM, "Set <alarm> to state ENABLED");
            AddCommandHelp(AlarmsMessageSchema.COMMAND_TEST_ALARM, "Set <alarm> to ON for a short period of time");
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
            if (states == null) states = _alarmStates;

            foreach (var state in states.Values)
            {
                if (AlarmsMessageSchema.IsAlarmStateOn(state)) return true;
            }
            return false;
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
                        String msg = String.Format("Alarm {0} on {1}", newState ? "on" : "off", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        OnAlarmStateChanged(dev.ID, newState ? AlarmState.CRITICAL : AlarmState.OFF, msg);
                        if (message.Tag == 0) return; //i.e. hasn't been specifically requested so do not call base method as this will broadcast (which is not necessary because OnAlarmStateChanged broadcasts)
                    }
                    break;

                case MessageType.NOTIFICATION:
                    dev = adm.GetDevice(message.Sender);
                    if (dev.ID.Equals(_buzzer.ID))
                    {
                        var schema = new AlarmsMessageSchema(message);
                        schema.AddBuzzer(_buzzer);
                    }
                    break;
            }

            base.HandleADMMessage(message, adm);
        }

        private void HandleRemoteAlarmMessage(MessageFilter remote, Message message)
        {
            RemoteAlarm a = (RemoteAlarm)remote;
            OnAlarmStateChanged(a.DeviceID, a.AlarmState, a.AlarmMessage);
        }

        private void OnAlarmStateChanged(String deviceID, AlarmState newState, String alarmMessage = null, String comments = null, bool testing = false)
        {
            //if this is called while testing then we end the test as his takes priority
            if (IsTesting)
            {
                EndAlarmTest(String.Format("Ending test because {0} changed state to {1}", deviceID, newState), null);
            }

            //keep track of the new state in a ID to state map
            _alarmStates[deviceID] = newState;
            _alarmMessages[deviceID] = alarmMessage;

            //a state change has occurred so we log it
            try
            {
                //Tracing?.TraceEvent(TraceEventType.Information, 0, "Logging alarm device {0} change of state to {1}", deviceID, newState);
                _asdb.LogStateChange(deviceID, newState.ToString(), alarmMessage, comments);
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
            var alert = AlarmsMessageSchema.AlertAlarmStateChange(deviceID, newState, alarmMessage, testing, _buzzer, _pilot);
            Broadcast(alert);
        }

        private void EnableAlarm(String id, bool enable)
        {
            //search local alarms
            foreach (var a in _localAlarms)
            {
                if (a.AlarmSwitch.ID.Equals(id))
                {
                    a.AlarmSwitch.Enable(enable);
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
            AlarmsMessageSchema schema = new AlarmsMessageSchema(response);
            switch (cmd)
            {
                case AlarmsMessageSchema.COMMAND_LIST_ALARMS:
                    var rows = _asdb.SelectDevices();
                    schema.AddAlarms(rows);
                    return true;

                case AlarmsMessageSchema.COMMAND_ALARM_STATUS:
                    schema.AddAlarmStatus(_alarmStates, _buzzer, _pilot, IsTesting);
                    return true;

                case AlarmsMessageSchema.COMMAND_SILENCE:
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

                case AlarmsMessageSchema.COMMAND_UNSILENCE:
                    if (ADMS.Count == 0) throw new Exception("No boards connected");
                    _buzzer.Unsilence();
                    schema.AddBuzzer(_buzzer);
                    message.Value = "Buzzer unsilenced";
                    return true;

                case AlarmsMessageSchema.COMMAND_DISABLE_ALARM:
                    if (args.Count == 0) throw new Exception("No alarm specified to disable");
                    id = args[0].ToString();
                    if (!_alarmStates.ContainsKey(id)) throw new Exception(String.Format("No alarm found with id {0}", id));
                    EnableAlarm(id, false);
                    OnAlarmStateChanged(id, AlarmState.DISABLED, null, String.Format("Command sent from {0}", message.Sender));
                    response.Value = String.Format("Alarm {0} disabled", id);
                    return true;

                case AlarmsMessageSchema.COMMAND_ENABLE_ALARM:
                    if (args.Count == 0) throw new Exception("No alarm specified to enable");
                    id = args[0].ToString();
                    if (!_alarmStates.ContainsKey(id)) throw new Exception(String.Format("No alarm found with id {0}", id));
                    EnableAlarm(id, true);
                    OnAlarmStateChanged(id, AlarmState.OFF, null, String.Format("Command sent from {0}", message.Sender));
                    response.Value = String.Format("Alarm {0} enabled", id);
                    return true;

                case AlarmsMessageSchema.COMMAND_TEST_ALARM:
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
                    var schema = new AlarmsMessageSchema(message);
                    var remoteStates = schema.GetAlarmStates();
                    if (remoteStates != null)
                    {
                        foreach (var kv in remoteStates)
                        {
                            if (_alarmStates.ContainsKey(kv.Key) && _alarmStates[kv.Key] != kv.Value)
                            {
                                //A remote alarm has a different state from the one that we have recorded in this service
                                //so we take the remote state as authority and upcate accordingly
                                String msg = String.Format("Alarm {0} has state {1} but remote state {2} so updating locally", kv.Key, _alarmStates[kv.Key], kv.Value);
                                Tracing?.TraceEvent(TraceEventType.Warning, 2000, msg);
                                OnAlarmStateChanged(kv.Key, kv.Value, msg);
                            }
                        }
                    }

                    break;

                case MessageType.ALERT:
                    break;
            }
            base.HandleClientMessage(cnn, message);
        }

        private void UpdateAlarmStates(Object sender, System.Timers.ElapsedEventArgs ea)
        {
            //request remote alarm states
            Message message = new Message(Chetch.Messaging.MessageType.COMMAND);
            foreach (var client in _remoteClients)
            {
                SendCommand(client, AlarmsMessageSchema.COMMAND_ALARM_STATUS);
            }

            //broadcast current states
            AlarmsMessageSchema schema = new AlarmsMessageSchema(new Message());
            schema.AddAlarmStatus(_alarmStates, _buzzer, _pilot, IsTesting);
            Broadcast(message);
        }

        //testing
        private void StartAlarmTest(String deviceID)
        {
            if (IsTesting) throw new Exception(String.Format("Cannot test alarm {0} as already testing {1}", deviceID, _testingAlarmID));
            if (!_alarmStates.ContainsKey(deviceID)) throw new Exception(String.Format("No alarm found with id {0}", deviceID));
            if (IsAlarmOn()) throw new Exception("Cannot test any alarm while an alarm is already on");
            if (_alarmStates[deviceID] != AlarmState.OFF) throw new Exception(String.Format("Cannot test alarm {0} as it is {1}", deviceID, _alarmStates[deviceID]));

            String msg = String.Format("Start alarm test on {0}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            OnAlarmStateChanged(deviceID, AlarmState.CRITICAL, msg, "Start alarm test", true);

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
            OnAlarmStateChanged(deviceID, AlarmState.OFF, null, logMsg, true);
        }
    } //end class
}