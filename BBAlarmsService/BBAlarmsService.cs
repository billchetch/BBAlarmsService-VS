using System;
using System.Collections.Generic;
using System.Linq;
using Chetch.Messaging;
using Chetch.Utilities;
using System.Diagnostics;
using Chetch.Arduino2;
using Chetch.Arduino2.Devices;
using Chetch.Arduino2.Devices.Buzzers;

namespace BBAlarmsService
{
    public class BBAlarmsService : ADMService
    {
        class LocalAlarm
        {
            public String AlarmID { get { return AlarmSwitch.ID; } }
            public String AlarmName { get; internal set; }
            public SwitchDevice AlarmSwitch { get; internal set; }
            
            public LocalAlarm(String alarmName, String alarmID, byte pin, int tolerance)
            {
                AlarmName = alarmName;
                AlarmSwitch = new SwitchDevice(alarmID, SwitchDevice.SwitchMode.PASSIVE, pin, tolerance);
            }

        }

        class RemoteAlarm : MessageFilter
        {
            public String AlarmID { get; internal set; }
            public String AlarmName { get; internal set; }
            public AlarmState AlarmState { get; internal set; } = AlarmState.OFF;
            public String AlarmMessage { get; internal set; }
            public bool IsOn { get { return AlarmsMessageSchema.IsAlarmStateOn(AlarmState); } }
            public bool IsOff { get { return !IsOn; } }
            public bool Enabled { get; internal set; } = true;

            private AlarmsMessageSchema _schema = new AlarmsMessageSchema();

            public RemoteAlarm(String alarmID, String alarmName, String clientName) : base(clientName, Chetch.Messaging.MessageType.ALERT, "AlarmID", alarmID)
            {
                AlarmID = alarmID;
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

        enum AlarmTest
        {
            NONE,
            ALARM,
            BUZZER,
            PILOT_LIGHT
        }

        public const int UPDATE_ALARM_STATES_INTERVAL = 30 * 1000;

        public const int PILOT_LIGHT_PIN = 6;
        public const int BUZZER_PIN = 5;
        
        private List<LocalAlarm> _localAlarms = new List<LocalAlarm>();
        private List<RemoteAlarm> _remoteAlarms = new List<RemoteAlarm>();
        private Dictionary<String, AlarmState> _alarmStates = new Dictionary<String, AlarmState>();
        private Dictionary<String, String> _alarmMessages = new Dictionary<String, String>();

        private ArduinoDeviceManager _adm;
        private SwitchDevice _pilot;
        private Buzzer _buzzer;

        private AlarmsServiceDB _asdb;

        private System.Timers.Timer _updateAlarmStatesTimer = null; //for remote alarms and also broadcast to clients
        private List<String> _remoteClients = new List<String>();

        private String _testingAlarmID = null;
        private AlarmTest _currentTest = AlarmTest.NONE;
        private System.Timers.Timer _testAlarmTimer = null;

        public bool IsTesting { get { return _currentTest != AlarmTest.NONE; } }

        public BBAlarmsService(bool test = false) : base("BBAlarms", test ? null : "BBAlarmsClient", "BBAlarmsService", test ? null : "BBAlarmsServiceLog")
        {
            try
            {
                Tracing?.TraceEvent(TraceEventType.Information, 0, "Connecting to Alarms database...");
                _asdb = AlarmsServiceDB.Create(Properties.Settings.Default, "AlarmsDBName");

                Tracing?.TraceEvent(TraceEventType.Information, 0, "Setting service DB to {0} and settings to default", _asdb.DBName);
                ServiceDB = _asdb;
                Settings = Properties.Settings.Default;

                Tracing?.TraceEvent(TraceEventType.Information, 0, "Creating alarms...");

                var rows = _asdb.SelectAlarms();
                foreach (var row in rows)
                {
                    String source = row.GetString("alarm_source");
                    String alarmID = row.GetString("alarm_id");
                    String alarmName = row.GetString("alarm_name");
                    if (source == null || source == String.Empty)
                    {
                        //The alarm is local and provided by an ADM input
                        byte pin = row.GetByte("pin_number");
                        if (pin == 0) throw new Exception("BBAlarmsService: Cannot have an alarm pin 0");
                        LocalAlarm la = new LocalAlarm(alarmName, alarmID, pin, row.GetInt("noise_threshold"));
                        la.AlarmSwitch.Switched += HandleLocalAlarm;
                        _localAlarms.Add(la);
                        Tracing?.TraceEvent(TraceEventType.Information, 0, "Created {0} local alarm with device id {1} and device name {2} for pin {3}", row["alarm_name"], la.AlarmSwitch.ID, la.AlarmSwitch.Name, pin);
                    }
                    else
                    {
                        //The alarm is remote so we need to subscribe to the remote service and listen
                        RemoteAlarm ra = new RemoteAlarm(alarmID, alarmName, source);
                        ra.HandleMatched += HandleRemoteAlarmMessage;
                        _remoteAlarms.Add(ra);
                        Subscribe(ra);
                        Tracing?.TraceEvent(TraceEventType.Information, 0, "Created {0} remote alarm @ {1} with id {2}", ra.AlarmName, source, alarmID);

                        if (!_remoteClients.Contains(source))_remoteClients.Add(source);
                    }
                    _alarmStates[alarmID] = AlarmState.OFF;
                    _alarmMessages[alarmID] = null;
                }


                //Started at end of CreateADM (which is called after Client is connected)
                _updateAlarmStatesTimer = new System.Timers.Timer();
                _updateAlarmStatesTimer.Interval = UPDATE_ALARM_STATES_INTERVAL;
                _updateAlarmStatesTimer.Elapsed += new System.Timers.ElapsedEventHandler(UpdateAlarmStates);

                //triggered by calling  a test
                _testAlarmTimer = new System.Timers.Timer();
                _testAlarmTimer.Elapsed += new System.Timers.ElapsedEventHandler(EndTest);
            }
            catch (Exception e)
            {
                Tracing?.TraceEvent(TraceEventType.Error, 0, e.Message);
                throw e;
            }
        }

        protected override void CreateADMs()
        {
            if (_adm != null) return;

            Tracing?.TraceEvent(TraceEventType.Information, 0, "Adding ADM and devices...");
            _adm = ArduinoDeviceManager.Create(ArduinoSerialConnection.BOARD_UNO, 115200, 64, 64);

            _pilot = new SwitchDevice("pilot", SwitchDevice.SwitchMode.ACTIVE, PILOT_LIGHT_PIN);
            _adm.AddDevice(_pilot);

            _buzzer = new Buzzer("buzzer", BUZZER_PIN);
            _adm.AddDevice(_buzzer);

            foreach (var a in _localAlarms)
            {
                Tracing?.TraceEvent(TraceEventType.Information, 0, "Adding alarm {0} {1} to {2}", a.AlarmSwitch.ID, a.AlarmName, _adm.UID);
                _adm.AddDevice(a.AlarmSwitch);
            }
            Tracing?.TraceEvent(TraceEventType.Information, 0, "Added {0} devices to {1}", _adm.DeviceCount, _adm.UID);
            AddADM(_adm);

            //now start the update alarms timer
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
            AddCommandHelp(AlarmsMessageSchema.COMMAND_TEST_BUZZER, "Sound buzzer for a short period of time");
            AddCommandHelp(AlarmsMessageSchema.COMMAND_TEST_PILOT_LIGHT, "Turn on pilot light for a short period of time");
            AddCommandHelp(AlarmsMessageSchema.COMMAND_END_TEST, "End current test");
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

        private void HandleLocalAlarm(Object sender, SwitchDevice.SwitchPosition pos)
        {
            SwitchDevice dev = (SwitchDevice)sender;
            if (dev != null && _alarmStates.ContainsKey(dev.ID))
            {
                String msg = String.Format("Alarm {0} {1} @ {2}", dev.ID, dev.IsOn ? "on" : "off", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                OnAlarmStateChanged(dev.ID, dev.IsOn ? AlarmState.CRITICAL : AlarmState.OFF, msg);
            }
        }

        private void HandleRemoteAlarmMessage(MessageFilter remote, Message message)
        {
            RemoteAlarm a = (RemoteAlarm)remote;

            bool isEvent = !_alarmStates.ContainsKey(a.AlarmID) || a.AlarmState != _alarmStates[a.AlarmID];
            if (isEvent) 
            {
                OnAlarmStateChanged(a.AlarmID, a.AlarmState, a.AlarmMessage);
            }
        }

        private void OnAlarmStateChanged(String alarmID, AlarmState newState, String alarmMessage = null, String comments = null, bool testing = false)
        {
            //if this is called while testing then we end the test as his takes priority
            if (IsTesting)
            {
                EndTest(String.Format("Ending test because {0} changed state to {1}", alarmID, newState), null);
            }

            //keep track of the new state in a ID to state map
            _alarmStates[alarmID] = newState;
            _alarmMessages[alarmID] = alarmMessage;

            //a state change has occurred so we log it
            try
            {
                //Tracing?.TraceEvent(TraceEventType.Information, 0, "Logging alarm device {0} change of state to {1}", deviceID, newState);
                _asdb.LogStateChange(alarmID, newState, alarmMessage, comments);
            }
            catch (Exception e)
            {
                Tracing?.TraceEvent(TraceEventType.Error, 0, "Logging alarm error: {0}", e.Message);
            }

            //turn buzzer on or off
            if (IsAlarmOn()) //if at least one alarm is on
            {
                _pilot.TurnOn();
                if (HasAlarmWithState(AlarmState.CRITICAL))
                {
                    _buzzer.TurnOn();
                } else
                {
                    _buzzer.TurnOff();
                }
            }
            else
            {
                _pilot.TurnOff();
                _buzzer.TurnOff();
            }

            //finally we broadcast to any listeners
            Message alert = AlarmsMessageSchema.AlertAlarmStateChange(alarmID, newState, alarmMessage, testing, _buzzer, _pilot);
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
                if (a.AlarmID.Equals(id))
                {
                    a.Enable(enable);
                    return;
                }
            }
        }

        override public bool HandleCommand(Connection cnn, Message message, String cmd, List<Object> args, Message response)
        {
            String id; //used for alarm id
            int secs; //used for duration e.g. testing
            AlarmsMessageSchema schema = new AlarmsMessageSchema(response);
            switch (cmd)
            {
                case AlarmsMessageSchema.COMMAND_LIST_ALARMS:
                    var rows = _asdb.SelectAlarms();
                    schema.AddAlarms(rows);
                    return true;

                case AlarmsMessageSchema.COMMAND_ALARM_STATUS:
                    schema.AddAlarmStatus(_alarmStates, _buzzer, _pilot, IsTesting);
                    return true;

                case AlarmsMessageSchema.COMMAND_SILENCE:
                    secs = args.Count > 0 ? System.Convert.ToInt16(args[0]) : 60 * 5;
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
                    AlarmState alarmState = args.Count > 1 ? (AlarmState)System.Convert.ToInt16(args[1]) : AlarmState.CRITICAL;
                    secs = args.Count > 2 ? System.Convert.ToInt16(args[2]) : 5;
                    StartTest(AlarmTest.ALARM, id, alarmState, secs);
                    response.Value = String.Format("Testing alarm {0} for {1} secs", id, secs);
                    return true;

                case AlarmsMessageSchema.COMMAND_TEST_BUZZER:
                    secs = args.Count > 0 ? System.Convert.ToInt16(args[0]) : 5;
                    StartTest(AlarmTest.BUZZER, null, AlarmState.OFF, secs);
                    response.Value = String.Format("Testing buzzer for {0} secs", secs);
                    return true;

                case AlarmsMessageSchema.COMMAND_TEST_PILOT_LIGHT:
                    secs = args.Count > 0 ? System.Convert.ToInt16(args[0]) : 5;
                    StartTest(AlarmTest.PILOT_LIGHT, null, AlarmState.OFF, secs);
                    response.Value = String.Format("Testing pilot for {0} secs", secs);
                    return true;

                case AlarmsMessageSchema.COMMAND_END_TEST:
                    EndTest(null, null);
                    response.Value = "Ending current test";
                    return true;

                default:
                    return base.HandleCommand(cnn, message, cmd, args, response);
            }
        }

        private void UpdateAlarmStates(Object sender, System.Timers.ElapsedEventArgs ea)
        {
            //request remote alarm states
            Message message = new Message(Chetch.Messaging.MessageType.COMMAND);
            foreach (var client in _remoteClients)
            {
                SendCommand(client, AlarmsMessageSchema.COMMAND_ALARM_STATUS);
            }

            //request local states
            foreach(LocalAlarm la in _localAlarms)
            {
                la.AlarmSwitch.RequestStatus();
            }

            System.Threading.Thread.Sleep(500); //allow for states to update (kind of loose here...)

            //broadcast current states
            AlarmsMessageSchema schema = new AlarmsMessageSchema(new Message());
            schema.AddAlarmStatus(_alarmStates, _buzzer, _pilot, IsTesting);
            Broadcast(message);
        }

        //testing
        private void StartTest(AlarmTest test, String alarmID, AlarmState alarmState = AlarmState.OFF, int testSecs = 5)
        {
            if (IsTesting) throw new Exception(String.Format("Cannot run test already testing {0}", _currentTest));
            if (IsAlarmOn()) throw new Exception("Cannot test any alarm while an alarm is already on");

            switch (test)
            {
                case AlarmTest.ALARM:
                    if (!_alarmStates.ContainsKey(alarmID)) throw new Exception(String.Format("No alarm found with id {0}", alarmID));
                    if (_alarmStates[alarmID] != AlarmState.OFF) throw new Exception(String.Format("Cannot test alarm {0} as it is {1}", alarmID, _alarmStates[alarmID]));

                    _testingAlarmID = alarmID;

                    if (alarmState == AlarmState.OFF)
                    {
                        var rand = new Random();
                        Array values = Enum.GetValues(typeof(AlarmState));
                        alarmState = (AlarmState)values.GetValue(1 + rand.Next(values.Length - 2));
                    }

                    String msg = String.Format("Start alarm test on {0}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    OnAlarmStateChanged(alarmID, alarmState, msg, "Start alarm test", true);
                    break;

                case AlarmTest.BUZZER:
                    _buzzer.TurnOn();
                    break;

                case AlarmTest.PILOT_LIGHT:
                    _pilot.TurnOn();
                    break;
            }
            
            //note: these have to be placed after call to state change (see OnStateChange method)
            _currentTest = test;
            _testAlarmTimer.Interval = testSecs * 1000;
            _testAlarmTimer.Start();
        }

        private void EndTest(Object sender, System.Timers.ElapsedEventArgs ea)
        {
            _testAlarmTimer.Stop();
            AlarmTest atest = _currentTest;
            _currentTest = AlarmTest.NONE;
            switch (atest)
            {
                case AlarmTest.ALARM:
                    var alarmID = _testingAlarmID;
                    _testingAlarmID = null;
                    String logMsg;
                    if (sender is String && sender != null)
                    {
                        logMsg = sender.ToString();
                    }
                    else
                    {
                        logMsg = "End alarm test after timeout";
                    }
                    OnAlarmStateChanged(alarmID, AlarmState.OFF, null, logMsg, true);
                    break;

                case AlarmTest.BUZZER:
                    _buzzer.TurnOff();
                    break;

                case AlarmTest.PILOT_LIGHT:
                    _pilot.TurnOff();
                    break;
            }
        }
    } //end class
}