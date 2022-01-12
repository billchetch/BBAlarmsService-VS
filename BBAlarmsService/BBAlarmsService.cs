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

        public const int USE_ARDUINO_PIN = 7;
        public const int PILOT_LIGHT_PIN = 6;
        public const int BUZZER_PIN = 5;
        private AlarmsServiceDB asdb;

        private List<LocalAlarm> localAlarms = new List<LocalAlarm>();
        private List<RemoteAlarm> remoteAlarms = new List<RemoteAlarm>();
        private Dictionary<String, AlarmState> alarmStates = new Dictionary<String, AlarmState>();
        private Dictionary<String, String> alarmMessages = new Dictionary<String, String>();

        private ArduinoDeviceManager adm;
        private SwitchDevice useArduino;
        private SwitchDevice pilot;
        private Buzzer buzzer;

        private System.Timers.Timer updateAlarmStatesTimer = null; //for remote alarms and also broadcast to clients
        private List<String> remoteClients = new List<String>();

        private String testingAlarmID = null;
        private AlarmTest currentTest = AlarmTest.NONE;
        private System.Timers.Timer testAlarmTimer = null;

        public bool IsTesting { get { return currentTest != AlarmTest.NONE; } }

        public BBAlarmsService() : base("BBAlarms", null, "ADMTestService", null)  //base("BBAlarms", "BBAlarmsClient", "BBAlarmsService", "BBAlarmsServiceLog")
        {
            try
            {
                Tracing?.TraceEvent(TraceEventType.Information, 0, "Connecting to Alarms database...");
                asdb = AlarmsServiceDB.Create(Properties.Settings.Default, "AlarmsDBName");
                Tracing?.TraceEvent(TraceEventType.Information, 0, "Connected to Alarms database. Now creating alarms...");

                var rows = asdb.SelectAlarms();
                foreach (var row in rows)
                {
                    String source = row.GetString("alarm_source");
                    String alarmID = row.GetString("alarm_id");
                    String alarmName = row.GetString("alarm_name");
                    if (source == null || source == String.Empty)
                    {
                        //The alarm is local and provided by an ADM input
                        int pin = row.GetInt("pin_number");
                        if (pin == 0) throw new Exception("BBAlarmsService: Cannot have an alarm pin 0");
                        LocalAlarm la = new LocalAlarm(alarmName, alarmID, pin, row.GetInt("noise_threshold"));
                        localAlarms.Add(la);
                        Tracing?.TraceEvent(TraceEventType.Information, 0, "Created {0} local alarm with device id {1} and device name {2} for pin {3}", row["alarm_name"], ss.ID, ss.Name, pin);
                    }
                    else
                    {
                        //The alarm is remote so we need to subscribe to the remote service and listen
                        RemoteAlarm ra = new RemoteAlarm(alarmID, alarmName, source);
                        ra.HandleMatched += HandleRemoteAlarmMessage;
                        remoteAlarms.Add(ra);
                        Subscribe(ra);
                        Tracing?.TraceEvent(TraceEventType.Information, 0, "Created {0} remote alarm @ {1} with id {2}", ra.AlarmName, source, alarmID);

                        if (!remoteClients.Contains(source))remoteClients.Add(source);
                    }
                    alarmStates[alarmID] = AlarmState.OFF;
                    alarmMessages[alarmID] = null;
                }


                Tracing?.TraceEvent(TraceEventType.Information, 0, "Alarms created. Now adding ADM and devices...");
                adm = ArduinoDeviceManager.Create(ArduinoSerialConnection.BOARD_UNO, 115200, 64, 64);

                useArduino = new SwitchDevice("useard", SwitchDevice.SwitchMode.ACTIVE, USE_ARDUINO_PIN);
                adm.AddDevice(useArduino);
                
                pilot = new SwitchDevice("pilot", SwitchDevice.SwitchMode.ACTIVE, PILOT_LIGHT_PIN);                
                adm.AddDevice(pilot);

                buzzer = new Buzzer("buzzer", BUZZER_PIN);
                adm.AddDevice(buzzer);

                foreach (var a in localAlarms)
                {
                    Tracing?.TraceEvent(TraceEventType.Information, 0, "Adding alarm {0} {1} to {2}", a.AlarmSwitch.ID, a.AlarmName, adm.BoardID);
                    adm.AddDevice(a.AlarmSwitch);
                }
                Tracing?.TraceEvent(TraceEventType.Information, 0, "Added {0} devices to {1}", adm.DeviceCount, adm.UID);

                updateAlarmStatesTimer = new System.Timers.Timer();
                updateAlarmStatesTimer.Interval = UPDATE_ALARM_STATES_INTERVAL;
                updateAlarmStatesTimer.Elapsed += new System.Timers.ElapsedEventHandler(UpdateAlarmStates);

                testAlarmTimer = new System.Timers.Timer();
                testAlarmTimer.Elapsed += new System.Timers.ElapsedEventHandler(EndTest);
            }
            catch (Exception e)
            {
                Tracing?.TraceEvent(TraceEventType.Error, 0, e.Message);
                throw e;
            }
        }

        

        protected override void OnClientConnect(ClientConnection cnn)
        {
            base.OnClientConnect(cnn);

            updateAlarmStatesTimer.Start();
        }

        protected override void OnStop()
        {
            if (useArduino != null)useArduino.TurnOff();

            base.OnStop();
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
            if (states == null) states = alarmStates;

            foreach (var state in states.Values)
            {
                if (state == alarmState) return true;
            }
            return false;
        }

        private bool IsAlarmOn(Dictionary<String, AlarmState> states = null)
        {
            if (states == null) states = alarmStates;

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
                        String msg = String.Format("Alarm {0} {1} @ {2}", dev.ID, newState ? "on" : "off", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
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
                _pilot.On();
                if (HasAlarmWithState(AlarmState.CRITICAL))
                {
                    _buzzer.On();
                } else
                {
                    _buzzer.Off();
                }
            }
            else
            {
                _pilot.Off();
                _buzzer.Off();
                _buzzer.Unsilence();
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
                    if (ADMS.Count == 0) throw new Exception("No boards connected");
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
                la.AlarmSwitch.RequestState();
            }

            System.Threading.Thread.Sleep(500); //allow for states to update (kind of loose here...)

            //ping the board so it doesn't reach inactivity timeout
            var adm = GetADM(null); //assume only one board
            if (adm != null) adm.Ping();

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
                    _buzzer.On();
                    break;

                case AlarmTest.PILOT_LIGHT:
                    _pilot.On();
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
                    _buzzer.Off();
                    break;

                case AlarmTest.PILOT_LIGHT:
                    _pilot.Off();
                    break;
            }
        }
    } //end class
}