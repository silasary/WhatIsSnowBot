using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WhatIsSnowBot
{
    [Serializable]
    internal class Match
    {
        public long A;
        public long B;

        public long Announcement = -1;
        public long Winner = -1;

        public long LastRecounted = 0;

        public Match(long A, long B)
        {
            this.A = A;
            this.B = B;
        }
    }
}
