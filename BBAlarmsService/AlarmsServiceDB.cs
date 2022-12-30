using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Chetch.Database;
using Chetch.Arduino2;

namespace BBAlarmsService
{
    public class AlarmsServiceDB : ADMServiceDB
    {
        static public new AlarmsServiceDB Create(System.Configuration.ApplicationSettingsBase settings, String dbnameKey = null)
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

            // - Alarm last state
            fields = "al.*";
            from = "alarm_log al";
            filter = "alarm_id={0} AND alarm_state='{1}' AND created<='{2}'";
            sort = "created DESC";
            this.AddSelectStatement("alarm_last_state", fields, from, filter, sort, null);

            // - Alarm first state
            fields = "al.*";
            from = "alarm_log al";
            filter = "alarm_id={0} AND alarm_state='{1}' AND created>='{2}'";
            sort = "created ASC";
            this.AddSelectStatement("alarm_first_state", fields, from, filter, sort, null);

            // - Alarm last raised
            fields = "al.*";
            from = "alarm_log al";
            filter = "alarm_id={0} AND alarm_state NOT IN ('OFF','DISABLED')";
            sort = "created DESC";
            this.AddSelectStatement("alarm_last_raised", fields, from, filter, sort, null);


            //Init base
            base.Initialize();
        }

        public List<DBRow> SelectAlarms()
        {
            List<DBRow> rows = Select("alarms", "*", "1");
            for(int i = 0; i < rows.Count; i++)
            {

                rows[i]["last_raised"] = GetAlarmLastRaised(rows[i].ID);
                rows[i]["last_lowered"] = GetAlarmLastLowered(rows[i].ID);
                rows[i]["last_disabled"] = GetAlarmLastDisabled(rows[i].ID);
            }

            return rows;
        }

        public DBRow SelectAlarm (String alarmID)
        {
            return SelectRow("alarm", "*", alarmID);
        }

        public DBRow SelectAlarmLastState(long alarmID, String alarmState, DateTime before = default(DateTime))
        {
            DateTime dt = before == default(DateTime) ? DateTime.MaxValue : before;
            return SelectRow("alarm_last_state", "*", alarmID.ToString(), alarmState, dt.ToString(DB.DATE_TIME_FORMAT));
        }

        public DBRow SelectAlarmFirstState(long alarmID, String alarmState, DateTime after = default(DateTime))
        {
            DateTime dt = after == default(DateTime) ? DateTime.MinValue : after;
            var row = SelectRow("alarm_first_state", "*", alarmID.ToString(), alarmState, dt.ToString(DB.DATE_TIME_FORMAT));
            return row;
        }

        public DateTime GetAlarmLastRaised(long alarmID)
        {
            var row = SelectRow("alarm_last_raised", "*", alarmID.ToString());
            return row == null ? DateTime.MinValue : row.GetDateTime("created");
        }

        public DateTime GetAlarmLastLowered(long alarmID)
        {
            DateTime lastRaised =  GetAlarmLastRaised(alarmID);
            if(lastRaised != DateTime.MinValue)
            {
                DBRow row = SelectAlarmFirstState(alarmID, "OFF", lastRaised);
                return row == null ? DateTime.MinValue : row.GetDateTime("created");
            } else
            {
                return DateTime.MinValue;
            }
        }

        public DateTime GetAlarmLastDisabled(long alarmID)
        {
            var row = SelectAlarmLastState(alarmID, "DISABLED");
            return row == null ? DateTime.MinValue : row.GetDateTime("created");
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