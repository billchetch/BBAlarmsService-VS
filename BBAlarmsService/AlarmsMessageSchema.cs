using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Chetch.Arduino;
using Chetch.Messaging;
using Chetch.Arduino.Devices;
using Chetch.Arduino.Devices.Buzzers;

namespace BBAlarmsService
{
    public enum AlarmState
    {
        DISABLED,
        OFF,
        MINOR,
        MODERATE,
        SEVERE,
        CRITICAL,
    }

    public class AlarmsMessageSchema : ADMService.MessageSchema
    {
        public const String COMMAND_ALARM_STATUS = "alarm-status";
        public const String COMMAND_LIST_ALARMS = "list-alarms";
        public const String COMMAND_SILENCE = "silence";
        public const String COMMAND_UNSILENCE = "unsilence";
        public const String COMMAND_DISABLE_ALARM = "disable-alarm";
        public const String COMMAND_ENABLE_ALARM = "enable-alarm";
        public const String COMMAND_TEST_ALARM = "test-alarm";
        public const String COMMAND_TEST_BUZZER = "test-buzzer";
        public const String COMMAND_TEST_PILOT_LIGHT = "test-pilot";
        public const String COMMAND_END_TEST = "end-test";

        static private Dictionary<String, AlarmState> _raisedAlerts = new Dictionary<String, AlarmState>();

        //this is for this service to broadcast to listeners
        static public Message RaiseAlert(ADMService alertingService, String alertID, String deviceID, AlarmState alarmState, String alarmMessage, bool testing = false, Buzzer buzzer = null, Chetch.Arduino.Devices.Switch pilot = null)
        {

            if (_raisedAlerts.ContainsKey(alertID) && _raisedAlerts[alertID] == alarmState) return null;

            Message msg = new Message(MessageType.ALERT);
            msg.AddValue(ADMService.MessageSchema.DEVICE_ID, deviceID);
            msg.AddValue("AlarmState", alarmState);
            msg.AddValue("AlarmMessage", alarmMessage);
            msg.AddValue("Testing", testing);

            var schema = new AlarmsMessageSchema(msg);
            if(buzzer != null)schema.AddBuzzer(buzzer);
            if(pilot != null)schema.AddPilot(pilot);

            try
            {
                alertingService.Broadcast(msg);
                _raisedAlerts[alertID] = alarmState;
            }
            catch (Exception)
            {

            }
            return msg;
        }

        static public Message RaiseAlert(ADMService alertingService, String deviceID, AlarmState alarmState, String alarmMessage, bool testing = false, Buzzer buzzer = null, Chetch.Arduino.Devices.Switch pilot = null)
        {
            return RaiseAlert(alertingService, deviceID, deviceID, alarmState, alarmMessage, testing, buzzer, pilot);
        }

        public static Message LowerAlert(ADMService alertingService, String alertID, String deviceID, AlarmState alarmState = AlarmState.OFF, String alarmMessage = null, bool testing = false)
        {
            if (!_raisedAlerts.ContainsKey(alertID)) return null;

            Message msg = new Message(MessageType.ALERT);
            msg.AddValue(ADMService.MessageSchema.DEVICE_ID, deviceID);
            msg.AddValue("AlarmState", alarmState);
            msg.AddValue("AlarmMessage", alarmMessage);
            msg.AddValue("Testing", testing);

            try
            {
                alertingService.Broadcast(msg);
                _raisedAlerts.Remove(alertID);
            } catch (Exception)
            {

            }

            return msg;
        }

        public static Message LowerAlert(ADMService alertingService, String deviceID, AlarmState alarmState = AlarmState.OFF, String alarmMessage = null, bool testing = false)
        {
            return LowerAlert(alertingService, deviceID, deviceID, alarmState, alarmMessage, testing);
        }

        static public bool IsAlarmStateOn(AlarmState state)
        {
            return state != AlarmState.OFF && state != AlarmState.DISABLED;
        }

        public AlarmsMessageSchema() { }

        public AlarmsMessageSchema(Message message) : base(message) { }

        public void AddAlarms(List<Chetch.Database.DBRow> rows)
        {
            Message.AddValue("Alarms", rows.Select(i => i.GenerateParamString(true)).ToList());
        }

        public List<String> GetAlarms()
        {
            return Message.GetList<String>("Alarms");
        }

        public void AddAlarmStatus(Dictionary<String, AlarmState> states, Buzzer buzzer, Chetch.Arduino.Devices.Switch pilot, bool testing = false)
        {
            AddAlarmStates(states);
            if(buzzer != null)AddBuzzer(buzzer);
            if(pilot != null)AddPilot(pilot);
            AddTesting(testing);
        }

        public void AddAlarmStates(Dictionary<String, AlarmState> states)
        {
            Message.AddValue("AlarmStates", states);
        }

        public Dictionary<String, AlarmState> GetAlarmStates()
        {
            return Message.HasValue("AlarmStates") ? Message.GetDictionary<AlarmState>("AlarmStates") : null;
        }

        public void AddBuzzer(Buzzer buzzer)
        {
            Message.AddValue("Buzzer", buzzer.ToString());
            Message.AddValue("BuzzerID", buzzer.ID);
            Message.AddValue("BuzzerOn", buzzer.IsOn);
            Message.AddValue("BuzzerSilenced", buzzer.IsSilenced);
        }

        public void AddPilot(Chetch.Arduino.Devices.Switch pilot)
        {
            Message.AddValue("Pilot", pilot.ToString());
            Message.AddValue("PilotID", pilot.ID);
            Message.AddValue("PilotOn", pilot.IsOn);
        }

        public bool IsAlert()
        {
            return Message.HasValue("AlarmState");
        }

        public String GetAlarmMessage()
        {
            return Message.GetString("AlarmMessage");
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
}
