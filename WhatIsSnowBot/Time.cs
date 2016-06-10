using System;

namespace WhatIsSnowBot
{
    public class Time
    {
        public Time()
        {
        }
        public static string LargestIntervalWithUnits(TimeSpan interval)
        {
            if (interval <= TimeSpan.Zero)
            {
                return "now";
            }

            double timeValue;
            string timeUnits;
            if (interval.TotalDays > 300.0)
            {
                timeValue = interval.TotalDays / 365.0;
                timeUnits = " year";
            }
            else if (interval.TotalDays > 27.0)
            {
                timeValue = CalcMonths(interval);
                timeUnits = " month";
            }
            else if (interval.TotalHours > 22.0)
            {
                timeValue = interval.TotalDays;
                timeUnits = " day";
            }
            else if (interval.TotalMinutes > 50.0)
            {
                timeValue = interval.TotalHours;
                timeUnits = " hour";
            }
            else
            {
                timeValue = interval.TotalMinutes;
                timeUnits = " minute";
            }
            return string.Format("{0:N1}{1}{2}",
                timeValue,
                timeUnits,
                (timeValue == 1 ? string.Empty : "s"));
        }

        public static double CalcMonths(TimeSpan interval)
        {
            return interval.TotalDays / 30.0; // Close enough

//            DateTime earlyDate = DateTime.Now;
//            DateTime lateDate = DateTime.Now.Add(interval);
//
//            // Start with 1 month's difference and keep incrementing
//            // until we overshoot the late date
//            int monthsDiff = 1;
//            while (earlyDate.AddMonths(monthsDiff) <= lateDate)
//            {
//                monthsDiff++;
//            }
//            
//            return monthsDiff - 1;

        }
    }
}

