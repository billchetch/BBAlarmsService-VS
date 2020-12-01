using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Chetch.Database;

namespace BBAlarmsService
{
    public class AlarmsServiceDB : DB
    {
        static public AlarmsServiceDB Create(System.Configuration.ApplicationSettingsBase settings, String dbnameKey = null)
        {
            AlarmsServiceDB db = dbnameKey != null ? DB.Create<AlarmsServiceDB>(settings, dbnameKey) : DB.Create<AlarmsServiceDB>(settings);
            return db;
        }

        override public void Initialize()
        {
            //SELECTS
            // - Alarms
            String fields = "a.*";
            String from = "alarms a";
            String filter = "active={0}";
            String sort = "alarm_name";
            this.AddSelectStatement("alarms", fields, from, filter, sort, null);

            // - Alarm
            fields = "a.*";
            from = "alarms a";
            filter = "alarm_id='{0}'";
            this.AddSelectStatement("alarm", fields, from, filter, null, null);

            //Init base
            base.Initialize();
        }

        public List<DBRow> SelectAlarms()
        {
            return Select("alarms", "*", "1");
        }

        public DBRow SelectAlarm (String alarmID)
        {
            return SelectRow("alarm", "*", alarmID);
        }

        public long LogStateChange(String alarmID, AlarmState newState, String alarmMessage = null, String comments = null)
        {
            var row = SelectAlarm(alarmID);
            if (row == null) throw new Exception("No alarm found with ID " + alarmID);

            var newRow = new DBRow();
            newRow["alarm_state"] = newState.ToString();
            newRow["alarm_id"] = row.ID;
            if (alarmMessage != null) newRow["alarm_message"] = alarmMessage;
            if (comments != null) newRow["comments"] = comments;

            return Insert("alarm_log", newRow);
        }
    }
}