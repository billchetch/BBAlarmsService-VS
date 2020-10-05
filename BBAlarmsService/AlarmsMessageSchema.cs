﻿using System;
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
        OFF,
        CRITICAL,
        SEVERE,
        MODERATE,
        MINOR,
        DISABLED
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

        //this is for this service to broadcast to listeners
        static public Message AlertAlarmStateChange(String deviceID, AlarmState alarmState, String alarmMessage, bool testing = false, Buzzer buzzer = null, Chetch.Arduino.Devices.Switch pilot = null)
        {
            Message msg = new Message(MessageType.ALERT);
            msg.AddValue(ADMService.MessageSchema.DEVICE_ID, deviceID);
            msg.AddValue("AlarmState", alarmState);
            msg.AddValue("AlarmMessage", alarmMessage);
            msg.AddValue("Testing", testing);

            var schema = new AlarmsMessageSchema(msg);
            if(buzzer != null)schema.AddBuzzer(buzzer);
            if(pilot != null)schema.AddPilot(pilot);
            return msg;
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
            AddBuzzer(buzzer);
            AddPilot(pilot);
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