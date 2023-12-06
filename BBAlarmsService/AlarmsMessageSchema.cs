using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Chetch.Arduino2;
using Chetch.Messaging;
using Chetch.Arduino2.Devices;
using Chetch.Arduino2.Devices.Buzzers;

namespace BBAlarmsService
{
    public enum AlarmState
    {
        DISABLED,
        DISCONNECTED,
        LOWERED,
        MINOR,
        MODERATE,
        SEVERE,
        CRITICAL,
    }

    public enum AlarmTest
    {
        NONE,
        ALARM,
        BUZZER,
        PILOT_LIGHT
    }

    
    public class AlarmsMessageSchema : ADMService.MessageSchema
    {
        public const String ALARMS_SERVICE_NAME = "BBAlarms";

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
        public const String COMMAND_RAISE_ALARM = "raise-alarm";
        public const String COMMAND_LOWER_ALARM = "lower-alarm";
        public const String COMMAND_MASTER = "master";

        public const int NO_CODE = 0;
        public const int CODE_SOURCE_OFFLINE = 1;
        public const int CODE_SOURCE_ONLINE = 2;

        static private Dictionary<String, Message> _raisedAlarms = new Dictionary<String, Message>();


        static public Message AlertAlarmStateChange(String alarmID, AlarmState alarmState, String alarmMessage, int alarmCode, bool testing = false, Buzzer buzzer = null, Chetch.Arduino2.Devices.SwitchDevice pilot = null)
        {
            Message msg = new Message(MessageType.ALERT);
            msg.AddValue("AlarmID", alarmID);
            msg.AddValue("AlarmState", alarmState);
            msg.AddValue("AlarmMessage", alarmMessage == null ? "n/a" : alarmMessage);
            msg.AddValue("AlarmCode", alarmCode);
            msg.AddValue("Testing", testing);

            var schema = new AlarmsMessageSchema(msg);
            if (buzzer != null) schema.AddBuzzer(buzzer);
            if (pilot != null) schema.AddPilot(pilot);

            return msg;
        }

        static public Message TestingStatus(AlarmTest alarmTest, bool testing, Buzzer buzzer, Chetch.Arduino2.Devices.SwitchDevice pilot)
        {
            Message msg = new Message(MessageType.NOTIFICATION);
            msg.AddValue("AlarmTest", alarmTest);
            msg.AddValue("Testing", testing);

            var schema = new AlarmsMessageSchema(msg);
            if (buzzer != null) schema.AddBuzzer(buzzer);
            if (pilot != null) schema.AddPilot(pilot);

            return msg;
        }


        public AlarmsMessageSchema() { }

        public AlarmsMessageSchema(Message message) : base(message) { }

        public AlarmsMessageSchema(MessageType messageType) : base(messageType) { }

        
        public void AddAlarms(List<Chetch.Database.DBRow> rows, String tzOffset)
        {
            var l = rows.Select(i => i.GenerateParamString(true, (k, v, b) => { 
                if(v is DateTime)
                {
                    DateTime dt = (DateTime)v;
                    String d = dt == DateTime.MinValue || dt == DateTime.MaxValue ? String.Empty : dt.ToString(Chetch.Database.DB.DATE_TIME_FORMAT) + " " + tzOffset;
                    return i.GenerateParamString(k, d, true);
                } else {
                    return i.GenerateParamString(k, v, true);
                } 
            })).ToList();
            Message.AddValue("Alarms", l); 
        }

        public List<String> GetAlarms()
        {
            return Message.GetList<String>("Alarms");
        }

        public void AddAlarmStatus(Dictionary<String, AlarmState> states, Dictionary<String, String> messages, Dictionary<String, int> codes, Buzzer buzzer, SwitchDevice pilot, bool testing = false)
        {
            AddAlarmStates(states);
            AddAlarmMessages(messages);
            AddAlarmCodes(codes);
            if(buzzer != null)AddBuzzer(buzzer);
            if(pilot != null)AddPilot(pilot);
            AddTesting(testing);
        }

        public void AddAlarmStatus(AlarmState alarmState, String alarmMessage, int alarmCode, Buzzer buzzer, SwitchDevice pilot, bool testing = false)
        {
            Message.AddValue("AlarmState", alarmState);
            Message.AddValue("AlarmMessage", alarmMessage);
            Message.AddValue("AlarmCode", alarmCode);

            if (buzzer != null) AddBuzzer(buzzer);
            if (pilot != null) AddPilot(pilot);
            AddTesting(testing);
        }

        public void AddAlarmStates(Dictionary<String, AlarmState> states)
        {
            Message.AddValue("AlarmStates", states);
        }

        public void AddAlarmMessages(Dictionary<String, String> messages)
        {
            Message.AddValue("AlarmMessages", messages);
        }

        public void AddAlarmCodes(Dictionary<String, int> codes)
        {
            Message.AddValue("AlarmCodes", codes);
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

        public void AddPilot(Chetch.Arduino2.Devices.SwitchDevice pilot)
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
        public int GetAlarmCode()
        {
            return Message.GetInt("AlarmCode");
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
