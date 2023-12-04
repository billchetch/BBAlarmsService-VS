using System;
using System.Collections.Generic;
using System.Linq;
using Chetch.Messaging;
using Chetch.Utilities;
using System.Diagnostics;
using Chetch.Arduino2;
using Chetch.Arduino2.Devices;
using Chetch.Arduino2.Devices.Buzzers;
using Chetch.Services;

namespace BBAlarmsService
{
    public class BBAlarmsService : ADMService
    {
        class LocalAlarm : AlarmManager.IAlarmRaiser
        {

            public AlarmManager AlarmManager { get; set; }

            public String AlarmID { get { return AlarmSwitch.ID; } set { } }
            public String AlarmName { get; set; }


            public SwitchDevice AlarmSwitch { get; internal set; }

            
            public LocalAlarm(String alarmName, String alarmID, byte pin, int tolerance)
            {
                AlarmName = alarmName;
                AlarmSwitch = new SwitchDevice(alarmID, SwitchDevice.SwitchMode.PASSIVE, pin, tolerance);
                AlarmSwitch.Switched += (Object sender, SwitchDevice.SwitchPosition newPosition) =>
                {
                    if (AlarmManager.IsAlarmDisabled(AlarmID))
                    {
                        return;
                    }

                    if (!AlarmSwitch.Enabled)
                    {
                        AlarmManager.Disable(AlarmID);
                    }
                    else if (AlarmSwitch.IsOn)
                    {
                        AlarmManager.Raise(AlarmID, AlarmState.CRITICAL, String.Format("{0} alarm has been raised", AlarmName));
                    }
                    else if (AlarmSwitch.IsOff)
                    {
                        AlarmManager.Lower(AlarmID, String.Format("{0} alarm has been lowered", AlarmName));
                    }
                };
            }

            public void RegisterAlarms()
            {
                AlarmManager.RegisterAlarm(this, AlarmID, AlarmName);
            }

            public void RequestAlarmStatus()
            {
                if (AlarmSwitch.IsReady)
                {
                    AlarmSwitch.RequestStatus();
                }
            }
        }

        class RemoteAlarm : MessageFilter, AlarmManager.IAlarmRaiser
        {
            private String _alarmID;

            public String AlarmID => _alarmID;
            
            public AlarmManager AlarmManager { get; set; }

            private AlarmState _alarmState;

            private String _alarmMessage;

            private int _alarmCode = AlarmsMessageSchema.NO_CODE;

            private AlarmsMessageSchema _schema = new AlarmsMessageSchema();

            public RemoteAlarm(String alarmID, String clientName) : base(clientName, Chetch.Messaging.MessageType.ALERT, "AlarmID", alarmID)
            {
                _alarmID = alarmID;
            }

            
            protected override void OnMatched(Message message)
            {
                if (AlarmManager.IsAlarmDisabled(_alarmID))
                {
                    return;
                }

                _schema.Message = message;
                _alarmState = _schema.GetAlarmState();
                _alarmMessage = _schema.GetAlarmMessage();
                _alarmCode = _schema.GetAlarmCode();
                
                base.OnMatched(message);

                AlarmManager?.UpdateAlarm(_alarmID, _alarmState, _alarmMessage, _alarmCode);
            }

            public void RegisterAlarms()
            {
                AlarmManager.RegisterAlarm(this, _alarmID);
            }

        }

        public const int UPDATE_ALARM_STATES_INTERVAL = 30 * 1000;

        public const int PILOT_LIGHT_PIN = 6;
        public const int BUZZER_PIN = 5;
        public const int MASTER_PIN = 7;

        private AlarmManager _alarmManager;
        private List<String> _alarmSources = new List<String>();
        
        private ArduinoDeviceManager _adm;
        public ArduinoDeviceManager ADM
        {
            get { return _adm; }
            set
            {
                if (_adm != null) throw new Exception("ADM already present");
                _adm = value;
            }
        }

        private SwitchDevice _pilot;
        private Buzzer _buzzer;
        private SwitchDevice _master; //this is a switch that when activated means that the pilot and buzzer can be activated only by this service (rather than directly)

        private AlarmsServiceDB _asdb;

        private System.Timers.Timer _updateAlarmStatesTimer = null; //for remote alarms and also broadcast to clients
        
        private String _testingAlarmID = null;
        private AlarmTest _currentTest = AlarmTest.NONE;
        private System.Timers.Timer _testAlarmTimer = null;

        public bool IsTesting { get { return _currentTest != AlarmTest.NONE; } }

        public BBAlarmsService(bool test = false) : base(AlarmsMessageSchema.ALARMS_SERVICE_NAME, test ? null : "BBAlarmsClient", test ? "ADMServiceTest" : "BBAlarmsService", test ? null : "BBAlarmsServiceLog")
        {
            try
            {
                AboutSummary = "BB Alarms Service v. 1.0.3";

                Tracing?.TraceEvent(TraceEventType.Information, 0, "Connecting to Alarms database...");
                _asdb = AlarmsServiceDB.Create(Properties.Settings.Default, "AlarmsDBName");

                Tracing?.TraceEvent(TraceEventType.Information, 0, "Setting service DB to {0} and settings to default", _asdb.DBName);
                ServiceDB = _asdb;
                Settings = Properties.Settings.Default;

                Tracing?.TraceEvent(TraceEventType.Information, 0, "Creating alarms...");

                _alarmManager = new AlarmManager();
                var rows = _asdb.SelectAlarms();
                foreach (var row in rows)
                {
                    String source = row.GetString("alarm_source");
                    String alarmID = row.GetString("alarm_id");
                    String alarmName = row.GetString("alarm_name");
                    bool canDisable = row.GetAsBool("can_disable");
                    
                    AlarmManager.IAlarmRaiser raiser;
                    if (String.IsNullOrEmpty(source)) //means local
                    {
                        //The alarm is local and provided by an ADM input
                        byte pin = row.GetByte("pin_number");
                        if (pin == 0) throw new Exception("BBAlarmsService: Cannot have an alarm pin 0");

                        raiser = new LocalAlarm(alarmName, alarmID, pin, row.GetInt("noise_threshold"));
                    }
                    else
                    {
                        //The alarm is remote so we need to subscribe to the remote service and listen
                        raiser = new RemoteAlarm(alarmID, source);
                        Subscribe((MessageFilter)raiser);
                        if (!_alarmSources.Contains(source))
                        {
                            _alarmSources.Add(source);
                        }
                    }
                    _alarmManager.AddRaiser(raiser);
                    var alarm = _alarmManager.GetAlarm(alarmID);
                    alarm.Name = alarmName;
                    alarm.LastRaised = row.GetDateTime("last_raised");
                    alarm.LastLowered = row.GetDateTime("last_lowered");
                    alarm.LastDisabled = row.GetDateTime("last_disabled");
                    alarm.CanDisable = canDisable;
                    bool enabled = row.GetAsBool("enabled");
                    alarm.Enable(enabled);
                }

                _alarmManager.AlarmChanged += (Object sender, AlarmManager.Alarm alarm) =>
                {
                    onAlarmChanged(alarm); //.ID, alarm.State, alarm.Message);
                };

                //Started at end of CreateADM (which is called after Client is connected)
                _updateAlarmStatesTimer = new System.Timers.Timer();
                _updateAlarmStatesTimer.Interval = UPDATE_ALARM_STATES_INTERVAL;
                _updateAlarmStatesTimer.Elapsed += new System.Timers.ElapsedEventHandler(requestAlarmStatus);

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

        protected override void OnStop()
        {
            _updateAlarmStatesTimer?.Stop();
            _testAlarmTimer?.Stop();

            if (_master != null)
            {
                try
                {
                    _master.TurnOff();
                } catch (Exception e)
                {
                    Tracing?.TraceEvent(TraceEventType.Error, 3110, e.Message);
                }
            }
            base.OnStop();
        }

        protected override void CreateADMs()
        {

            Tracing?.TraceEvent(TraceEventType.Information, 0, "Adding ADM and devices...");
            //if (_adm == null)
            //{
            _adm = ArduinoDeviceManager.Create(ArduinoSerialConnection.BOARD_ARDUINO, 115200, 64, 64);
            Tracing?.TraceEvent(TraceEventType.Information, 0, "Created USB connected ADM {0}", _adm.UID);
            /*}
            else
            {
                Tracing?.TraceEvent(TraceEventType.Information, 0, "Use supplied ADM {0}", _adm.UID);
            }*/

            _pilot = new SwitchDevice("pilot", SwitchDevice.SwitchMode.ACTIVE, PILOT_LIGHT_PIN);
            _adm.AddDevice(_pilot);

            _buzzer = new Buzzer("buzzer", BUZZER_PIN);
            _adm.AddDevice(_buzzer);


            _master = new SwitchDevice("master", SwitchDevice.SwitchMode.ACTIVE, MASTER_PIN);
            _adm.AddDevice(_master);

            foreach (var raiser in _alarmManager.AlarmRaisers)
            {
                if (raiser is LocalAlarm)
                {
                    var la = (LocalAlarm)raiser;
                    Tracing?.TraceEvent(TraceEventType.Information, 0, "Adding alarm {0} {1} to {2}", la.AlarmSwitch.ID, la.AlarmName, _adm.UID);
                    _adm.AddDevice(la.AlarmSwitch);
                }
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
            AddCommandHelp(AlarmsMessageSchema.COMMAND_TEST_ALARM, "Set <alarm> (to <state>) for a short period of time (<secs>)");
            AddCommandHelp(AlarmsMessageSchema.COMMAND_TEST_BUZZER, "Sound buzzer for a short period of time");
            AddCommandHelp(AlarmsMessageSchema.COMMAND_TEST_PILOT_LIGHT, "Turn on pilot light for a short period of time");
            AddCommandHelp(AlarmsMessageSchema.COMMAND_END_TEST, "End current test");
            AddCommandHelp(AlarmsMessageSchema.COMMAND_MASTER, "Turn master <on/off>");
        }

        /// <summary>
        /// Use this method to run various tests during dev
        /// </summary>
        /// <param name="args"></param>
        public override void Test(string[] args = null)
        {
            base.Test(args);
        }

        private void onAlarmChanged(AlarmManager.Alarm alarm, String comments = "") //String alarmID, AlarmState newState, String alarmMessage = null, String comments = null, bool testing = false)
        {
            //if this is called while testing then we end the test as his takes priority
            if (IsTesting && !alarm.IsTesting)
            {
                EndTest(String.Format("Ending test because {0} changed state to {1}", alarm.ID, alarm.State), null);
            }

            //a state change has occurred so we log it if it's not testing
            if (!alarm.IsTesting)
            {
                try
                {
                    Tracing?.TraceEvent(TraceEventType.Information, 0, "Logging alarm  {0} change of state to {1}, code {2}", alarm.ID, alarm.State, alarm.Code);
                    _asdb.LogChange(alarm.ID, alarm.State, alarm.Message, alarm.Code, comments);
                }
                catch (Exception e)
                {
                    Tracing?.TraceEvent(TraceEventType.Error, 0, "Logging alarm error: {0}", e.Message);
                }
            }

            //turn buzzer on or off
            if (_alarmManager.IsAlarmRaised) //if at least one alarm is on (or we are testing)
            {
                Tracing?.TraceEvent(TraceEventType.Information, 0, "Alarm is raised so turn master and pilot on");
                _master.TurnOn(); //this prevents bypassing
                _pilot.TurnOn();
                if (!_buzzer.IsSilenced && _alarmManager.HasAlarmWithState(AlarmState.CRITICAL))
                {
                    Tracing?.TraceEvent(TraceEventType.Information, 0, "Buzzer is not silenced and there is one critical alarm to turn on");
                    _buzzer.TurnOn();
                }
                else
                {
                    Tracing?.TraceEvent(TraceEventType.Information, 0, _buzzer.IsSilenced ? "Buzzer is silenced so ensure it is off" : "No critical alarms so ensure buzzer is off");
                    _buzzer.TurnOff();
                }
            }
            else
            {
                Tracing?.TraceEvent(TraceEventType.Information, 0, "No alarms raised so turn off pilot, buzzer and master");
                _pilot.TurnOff();
                _buzzer.TurnOff();
                _master.TurnOff();
            }

            //finally we broadcast to any listeners
            Tracing?.TraceEvent(TraceEventType.Information, 0, "Broadcast event to all listeners...");
            Message alert = AlarmsMessageSchema.AlertAlarmStateChange(alarm.ID, alarm.State, alarm.Message, alarm.Code, alarm.IsTesting, _buzzer, _pilot);
            Broadcast(alert);
        }


        private void onAlarmSourceOffline(String source)
        {
            foreach (var sub in Subscriptions)
            {
                if (sub is RemoteAlarm && sub.Sender == source)
                {
                    var ra = (RemoteAlarm)sub;
                    if (!_alarmManager.IsAlarmDisabled(ra.AlarmID))
                    {
                        _alarmManager.Lower(ra.AlarmID, String.Format("Lowering alarm as client {0} is offline", source), AlarmsMessageSchema.CODE_SOURCE_OFFLINE);
                    }
                }
            }
        }

        public override void HandleClientMessage(Connection cnn, Message message)
        {
            base.HandleClientMessage(cnn, message);

            
            switch (message.Type)
            {
                case MessageType.ERROR:
                    if(message.SubType == (int)ErrorCode.CLIENT_NOT_CONNECTED)
                    {
                        String source = message.GetString("IntendedTarget");
                        onAlarmSourceOffline(source);
                    }
                    break;

                case MessageType.NOTIFICATION:
                    if(message.SubType == (int)Server.NotificationEvent.CLIENT_CLOSED)
                    {
                        onAlarmSourceOffline(message.Sender);
                    } else if(message.SubType == (int)Server.NotificationEvent.CLIENT_CONNECTED)
                    {
                        requestAlarmStatus(null, null);
                    }
                    break;
            }
        }

        override public bool HandleCommand(Connection cnn, Message message, String cmd, List<Object> args, Message response)
        {
            String id; //used for alarm id
            int secs; //used for duration e.g. testing
            AlarmsMessageSchema schema = new AlarmsMessageSchema(response);
            AlarmManager.Alarm alarm = null;

            switch (cmd)
            {
                case AlarmsMessageSchema.COMMAND_LIST_ALARMS:
                    var rows = _asdb.SelectAlarms();
                    schema.AddAlarms(rows, _asdb.GetTimezoneOffset());
                    return true;

                case AlarmsMessageSchema.COMMAND_ALARM_STATUS:
                    if (args.Count > 0)
                    {
                        id = args[0].ToString();
                        alarm = _alarmManager.GetAlarm(id);
                        if (alarm == null)
                        {
                            throw new Exception(String.Format("There is no alarm with ID {0}", id));
                        }
                        schema.AddAlarmStatus(alarm.State, alarm.Message, alarm.Code, _buzzer, _pilot, IsTesting);
                    }
                    else
                    {
                        schema.AddAlarmStatus(_alarmManager.AlarmStates, _alarmManager.AlarmMessages, _alarmManager.AlarmCodes, _buzzer, _pilot, IsTesting);
                    }
                    return true;

                case AlarmsMessageSchema.COMMAND_SILENCE:
                    secs = args.Count > 0 ? System.Convert.ToInt16(args[0]) : 60 * 5;
                    if (_alarmManager.IsAlarmRaised && !_buzzer.IsSilenced && secs > 0)
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
                    if (args.Count == 0)
                    {
                        throw new Exception("No alarm specified to disable");
                    }
                    id = args[0].ToString();
                    if (!_alarmManager.HasAlarm(id))
                    {
                        throw new Exception(String.Format("No alarm found with id {0}", id));
                    }
                    _alarmManager.Disable(id);
                    _asdb.DisableAlarm(id); //save to db for persistence
                    return true;

                case AlarmsMessageSchema.COMMAND_ENABLE_ALARM:
                    if (args.Count == 0) throw new Exception("No alarm specified to enable");
                    id = args[0].ToString();
                    if (!_alarmManager.HasAlarm(id))
                    {
                        throw new Exception(String.Format("No alarm found with id {0}", id));
                    }
                    _alarmManager.Enable(id);
                    _asdb.EnableAlarm(id); //save to db for persistence
                    requestAlarmStatus(id); //because it might be a remote alarm
                    return true;

                case AlarmsMessageSchema.COMMAND_TEST_ALARM:
                    if (args.Count == 0) throw new Exception("No alarm specified to test");
                    id = args[0].ToString();
                    alarm = _alarmManager.GetAlarm(id);
                    if (alarm == null)
                    {
                        throw new Exception(String.Format("No alarm found with id {0}", id));
                    }
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

                case AlarmsMessageSchema.COMMAND_MASTER:
                    if (args.Count == 0) throw new Exception("Not specified on or off for master");
                    String maction = args[0].ToString().ToLower();
                    if (maction.Equals("on"))
                    {
                        _master.TurnOn();
                    } else if (maction.Equals("off"))
                    {
                        _master.TurnOff();
                    }
                    else
                    {
                        throw new Exception(String.Format("{0} is an unrecognised action", maction));
                    }
                    return true;

                default:
                    return base.HandleCommand(cnn, message, cmd, args, response);
            }
        }

        private void requestAlarmStatus(Object sender, System.Timers.ElapsedEventArgs ea)
        {
            foreach(var source in _alarmSources)
            {
                SendCommand(source, AlarmsMessageSchema.COMMAND_ALARM_STATUS);
            }
        }

        private void requestAlarmStatus(String alarmID)
        {
            foreach(var raiser in _alarmManager.AlarmRaisers)
            {
                if(raiser is RemoteAlarm && ((RemoteAlarm)raiser).AlarmID == alarmID)
                {
                    var source = ((RemoteAlarm)raiser).Sender; //this is the source or client name cos it is who sends the message
                    SendCommand(source, AlarmsMessageSchema.COMMAND_ALARM_STATUS, alarmID);
                    break;
                }
                if(raiser is LocalAlarm && ((LocalAlarm)raiser).AlarmID == alarmID)
                {
                    ((LocalAlarm)raiser).RequestAlarmStatus();
                    break;
                }
            }
        }

        //testing
        private void StartTest(AlarmTest test, String alarmID, AlarmState alarmState = AlarmState.OFF, int testSecs = 5)
        {
            if (IsTesting) throw new Exception(String.Format("Cannot run test already testing {0}", _currentTest));
            if (_alarmManager.IsAlarmRaised) throw new Exception("Cannot test any alarm if at least one alarm is already on");

            try
            {
                _currentTest = test;
                switch (_currentTest)
                {
                    case AlarmTest.ALARM:
                        _testingAlarmID = alarmID;

                        if (alarmState == AlarmState.OFF)
                        {
                            var rand = new Random();
                            Array values = Enum.GetValues(typeof(AlarmState));
                            alarmState = (AlarmState)values.GetValue(1 + rand.Next(values.Length - 2));
                        }

                        String msg = String.Format("Start alarm test on {0} for {1} secs", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), testSecs);
                        _alarmManager.StartTest(alarmID, alarmState, msg);
                        break;

                    case AlarmTest.BUZZER:
                        _buzzer.TurnOn();
                        break;

                    case AlarmTest.PILOT_LIGHT:
                        _pilot.TurnOn();
                        break;
                }
            } catch (Exception e)
            {
                //reset then throw
                _currentTest = AlarmTest.NONE;
                throw e;
            }

            
            //let listeners know a test has started
            var message = AlarmsMessageSchema.TestingStatus(test, true, _buzzer, _pilot);
            Broadcast(message);
            
            //note: these have to be placed after call to state change (see OnAlarmStateChange method)
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
                    _alarmManager.EndTest(alarmID);
                    break;

                case AlarmTest.BUZZER:
                    _buzzer.TurnOff();
                    break;

                case AlarmTest.PILOT_LIGHT:
                    _pilot.TurnOff();
                    break;
            }

            //broadcast end of test
            //let listeners know a test has started
            var msg = AlarmsMessageSchema.TestingStatus(atest, false, _buzzer, _pilot);
            Broadcast(msg);
        }
    } //end class
}