using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Chetch.Services;
using System.Configuration.Install;
using System.ComponentModel;

namespace BBAlarmsService
{

    [RunInstaller(true)]
    public class BBAlarmsServiceInstaller : ServiceInstaller
    {
        public BBAlarmsServiceInstaller() : base("BBAlarmsService",
                                    "Bulan Baru Alarms Service",
                                    "Runs an ADM service to be used to monitor alarms")
        {
            //empty
        }
    }
}