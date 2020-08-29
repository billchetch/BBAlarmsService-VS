using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Chetch.Messaging;
using Chetch.Arduino;
using Chetch.Arduino.Devices;
using System.Diagnostics;
using Chetch.Arduino.Devices.Buzzers;

namespace BBAlarmsService
{
    class BBAlarmsService : ADMService
    {
        public const int BUZZER_PIN = 4;
        private AlarmsServiceDB _asdb;
        
        List<SwitchSensor> _alarms = new List<SwitchSensor>();
        Buzzer _buzzer;
        
        public BBAlarmsService() : base("BBALARMS", "BBAlarmsClient", "BBAlarmsService", "BBAlarmsServiceLog")
        {
            SupportedBoards = ArduinoDeviceManager.DEFAULT_BOARD_SET;
            try
            {
                Tracing?.TraceEvent(TraceEventType.Information, 0, "Connecting to Alarms database...");
                _asdb = AlarmsServiceDB.Create(Properties.Settings.Default, "AlarmsDBName");
                Tracing?.TraceEvent(TraceEventType.Information, 0, "Connected to Alarms database. Now creating alarms...");

                var rows = _asdb.SelectDevices();
                foreach (var row in rows)
                {
                    int pin = (int)row["pin_number"];
                    SwitchSensor a = new SwitchSensor(pin, (int)row["noise_threshold"], (String)row["device_id"], (String)row["device_name"]);
                    _alarms.Add(a);
                    Tracing?.TraceEvent(TraceEventType.Information, 0, "Created {0} alarm with id {1} and name {2} for pin {3}", row["alarm_name"], a.ID, a.Name, pin);
                }

                
            }
            catch (Exception e)
            {
                Tracing?.TraceEvent(TraceEventType.Error, 0, e.Message);
                throw e;
            }
        }
        
        protected override void AddADMDevices(ArduinoDeviceManager adm, ADMMessage message)
        {
            _buzzer = new Buzzer(BUZZER_PIN);
            adm.AddDevice(_buzzer);

            foreach (var a in _alarms)
            {
                Tracing?.TraceEvent(TraceEventType.Information, 0, "Adding alarm {0} {1} to {2}", a.ID, a.Name, adm.BoardID);
                adm.AddDevice(a);
            }
        }

        public override void AddCommandHelp(List<string> commandHelp)
        {
            base.AddCommandHelp(commandHelp);

            commandHelp.Add("list-alarms:  Lists active alarms in the alarms database");
            commandHelp.Add("alarm-status:  Lists alarms currently on and currently off");
            commandHelp.Add("silence: Turn buzzer off for <seconds>");
        }

        private bool IsAlarmOn
        {
            get
            {
                if (ADMS.Count == 0) return false;
                var adm = GetADM(null);
                if (adm == null || !adm.IsConnected) return false;

                bool alarmOn = false;
                foreach (var a in _alarms)
                {
                    if (a.IsConnected && a.IsOn)
                    {
                        alarmOn = true;
                        break;
                    }
                }
                return alarmOn;
            }
        }

        protected override void HandleADMMessage(ADMMessage message, ArduinoDeviceManager adm)
        {
            switch (message.Type)
            {
                case Chetch.Messaging.MessageType.DATA:
                    var dev = adm.GetDevice(message.Sender);
                    if (dev != null && message.HasValue("State"))
                    {
                        bool newState = message.GetBool("State");
                        //a state change has occurred so we log it
                        Tracing?.TraceEvent(TraceEventType.Information, 0, "Logging alarm device {0} change of state to {1}", dev.ID, newState);
                        _asdb.LogStateChange(dev.ID, newState);

                        //turn buzzer on or off
                        if (IsAlarmOn)
                        {
                            _buzzer.On();
                        }
                        else
                        {
                            _buzzer.Off();
                        }
                    }
                    break;
            }

            base.HandleADMMessage(message, adm);
        }

        override public bool HandleCommand(Connection cnn, Message message, String cmd, List<Object> args, Message response)
        {
            switch (cmd)
            {
                case "list-alarms":
                    var rows = _asdb.SelectDevices();
                    response.AddValue("AlarmDBIDs", rows.Select(i => i.ID));
                    response.AddValue("AlarmDeviceIDs", rows.Select(i => i["device_id"]));
                    response.AddValue("AlarmPins", rows.Select(i => i["pin_number"]));
                    response.AddValue("AlarmNames", rows.Select(i => i["alarm_name"]));
                    return true;

                case "alarm-status":
                    if (ADMS.Count == 0) throw new Exception("No boards connected");
                    response.AddValue("AlarmsOn", _alarms.Where<SwitchSensor>(i => i.IsOn).Select(i => i.ID));
                    response.AddValue("AlarmsOff", _alarms.Where<SwitchSensor>(i => i.IsOff).Select(i => i.ID));
                    return true;

                case "silence":
                    if (ADMS.Count == 0) throw new Exception("No boards connected");
                    int secs = args.Count > 0 ? System.Convert.ToInt16(args[0]) : 60*5;
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

                default:
                    return base.HandleCommand(cnn, message, cmd, args, response);
            }
        }

        
    } //end class
}
