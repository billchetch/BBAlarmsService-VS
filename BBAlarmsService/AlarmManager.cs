using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Chetch.Services;

namespace BBAlarmsService
{
    public class AlarmManager
    {
        public interface IAlarmRaiser
        {
            AlarmManager AlarmManager { get; set; }

            void RegisterAlarms();
        }

        public class Alarm
        {
            public String ID { get; internal set; }

            public String Name { get; set; }


            private AlarmState _state = AlarmState.OFF;
            private AlarmState _prevState = AlarmState.OFF;
            public AlarmState State 
            {
                get
                {
                    return _state;
                } 
                set
                {
                    _prevState = _state;
                    _state = value;
                    if (IsRaised)
                    {
                        LastRaised = DateTime.Now;
                    } else if (IsLowered)
                    {
                        LastLowered = DateTime.Now;
                    } else if (IsDisabled)
                    {
                        LastDisabled = DateTime.Now;
                    }
                }
            }

            public IAlarmRaiser Raiser { get; set; }

            public bool Testing { get; internal set; } = false;

            public bool IsTesting => Testing;

            public bool IsLowered => State == AlarmState.OFF;

            public bool IsDisabled => State == AlarmState.DISABLED;

            public bool IsRaised => !IsLowered && !IsDisabled;


            public String Message { get; set; }

            public DateTime LastRaised { get; set; }

            public DateTime LastLowered { get; set; }

            public DateTime LastDisabled { get; set; }

            
            public bool HasChangedState => _prevState != _state;

            public Alarm(String alarmID)
            {
                ID = alarmID;
            }

            public void Enable(bool enable = true)
            {
                if (enable) {
                    if (State == AlarmState.DISABLED)
                    {
                        State =AlarmState.OFF;
                    }
                } else
                {
                    State = AlarmState.DISABLED;
                }
            }

            public void Disable()
            {
                Enable(false);
            }

            public void StartTest(AlarmState state, String msg = "Start testing")
            {
                Testing = true;
                Raise(state, msg);
            }

            public void EndTest(String msg = "End testing")
            {
                Lower(msg);
                Testing = false;
            }

            public void Raise(AlarmState state, String message)
            {
                Raiser.AlarmManager.Raise(ID, state, message);
            }

            public void Lower(String message)
            {
                Raiser.AlarmManager.Lower(ID, message);
            }
        }

        public event EventHandler<Alarm> AlarmStateChanged;

        public List<IAlarmRaiser> AlarmRaisers { get; internal set; } = new List<IAlarmRaiser>();
        private Dictionary<String, Alarm> _alarms = new Dictionary<String, Alarm>();
        
        public List<Alarm> Alarms { get => _alarms.Values.ToList();  }
        public AlarmManager()
        {
            
        }


        public Alarm RegisterAlarm(IAlarmRaiser raiser, String alarmID, String alarmName = null)
        {
            if (raiser == null)
            {
                throw new ArgumentNullException("Raiser cannot be null");
            }

            if (alarmID == null)
            {
                throw new ArgumentNullException("Alarm ID cannot be null");
            }

            if (_alarms.ContainsKey(alarmID))
            {
                throw new Exception(String.Format("There is already an alarm with ID {0}", alarmID));
            }
            
            Alarm alarm = new Alarm(alarmID);
            alarm.Raiser = raiser;
            alarm.Name = alarmName;

            _alarms[alarmID] = alarm;
            return alarm;
        }

        public void AddRaiser(IAlarmRaiser raiser)
        {
            if (!AlarmRaisers.Contains(raiser))
            {
                AlarmRaisers.Add(raiser);
                raiser.AlarmManager = this;
                raiser.RegisterAlarms();
            }
        }


        public void AddRaisers(IEnumerable<Object> items)
        {
            foreach(var item in items)
            {
                if(item is IAlarmRaiser)
                {
                    AddRaiser((IAlarmRaiser)item);
                }
            }
        }

        public Alarm GetAlarm(String id, bool throwException = false)
        {
            if(throwException && !_alarms.ContainsKey(id))
            {
                throw new Exception(String.Format("Alarm {0} not found", id));
            }
            return _alarms.ContainsKey(id) ? _alarms[id] : null;
        }

        public bool HasAlarm(String id)
        {
            return _alarms.ContainsKey(id);
        }

        public bool HasAlarmWithState(AlarmState alarmState)
        {

            foreach (Alarm a in _alarms.Values)
            {
                if (a.State == alarmState) return true;
            }
            return false;
        }

        public bool IsAlarmRaised
        {
            get
            {
                foreach (Alarm a in _alarms.Values)
                {
                    if (a.IsRaised) return true;
                }
                return false;
            }
        }

        public Alarm UpdateAlarm(String alarmID, AlarmState alarmState, String alarmMessage)
        {
            Alarm alarm;
            if (!_alarms.ContainsKey(alarmID))
            {
                throw new Exception(String.Format("Alarm {0} has not been registered", alarmID));
            } else
            {
                alarm =_alarms[alarmID];
            }

            alarm.State = alarmState;
            alarm.Message = alarmMessage;
            
            if(AlarmStateChanged != null && alarm.HasChangedState)
            {
                AlarmStateChanged.Invoke(this, alarm);
            }

            return alarm;
        }
        
        public Alarm Raise(String alarmID, AlarmState alarmState, String alarmMessage)
        {
            if(alarmState == AlarmState.OFF || alarmState == AlarmState.DISABLED)
            {
                throw new ArgumentException(String.Format("Alarm state {0} is not valid for raising an alarm", alarmState));
            }

            return UpdateAlarm(alarmID, alarmState, alarmMessage);
        }

        public Alarm Lower(String alarmID, String alarmMessage)
        {
            return UpdateAlarm(alarmID, AlarmState.OFF, alarmMessage);
        }

        public Alarm Disable(String alarmID)
        {
            return UpdateAlarm(alarmID, AlarmState.DISABLED, null);
        }

        public Alarm StartTest(String alarmID, AlarmState alarmState, String alarmMessage)
        {
            var alarm = GetAlarm(alarmID);
            if (alarm.IsRaised)
            {
                throw new Exception(String.Format("Alarm {0} already raised", alarmID));
            }
            alarm.StartTest(alarmState, alarmMessage);
            return alarm;
        }

        public Alarm EndTest(String alarmID)
        {
            var alarm = GetAlarm(alarmID);
            alarm.EndTest();
            return alarm;
        }

        
        public void NotifyAlarmsService(ChetchMessagingClient cmc, Alarm alarm = null)
        {
            if (alarm == null)
            {
                foreach(var a in _alarms.Values)
                {
                    NotifyAlarmsService(cmc, a);
                }
            }
            else
            {
                var message = AlarmsMessageSchema.AlertAlarmStateChange(alarm.ID, alarm.State, alarm.Message);
                message.Target = AlarmsMessageSchema.ALARMS_SERVICE_NAME;
                cmc.SendMessage(message);
            }
        }

    }
}
