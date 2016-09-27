using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TweetSharp;


namespace WhatIsSnowBot
{
    static class Program
    {
        private static TwitterService service;
        private static OAuthAccessToken access;

        public static List<Match> History { get; private set; }
        public static IEnumerable<TwitterStatus> AllTweets { get; private set; }
        public static TwitterStatus[] AvailableTweets { get; private set; }
        public static Match CurrentBout { get; private set; }
        public static TwitterStatus Announcement { get; private set; }

        public static TimeSpan RoundDuration = new TimeSpan(11, 59, 0);
        private static readonly TimeSpan OneHour = new TimeSpan(1,0,0);
        private static readonly TimeSpan OneMinute = new TimeSpan(0,1,0);

        private static Random rand = new Random();
        public static DateTime LastRecount = new DateTime();
        private static long LatestRetrievedTweetThisSession = -1;
        

        static void Main(string[] args)
        {
            var AppInfo = new {
                CLIENT_ID = Environment.GetEnvironmentVariable("CLIENT_ID"),
                CLIENT_SECRET = Environment.GetEnvironmentVariable("CLIENT_SECRET")
            };

            if (File.Exists("AppAuth.json"))
            {
               AppInfo = JsonConvert.DeserializeAnonymousType(File.ReadAllText("AppAuth.json"), AppInfo);
            }

            if (AppInfo.CLIENT_ID == null || AppInfo.CLIENT_SECRET == null)
            {
                string id = AppInfo.CLIENT_ID;
                if (AppInfo.CLIENT_ID == null)
                {
                    Console.Write("AppID: ");
                    id = Console.ReadLine();
                }

                string secret = AppInfo.CLIENT_SECRET;
                if (AppInfo.CLIENT_SECRET == null)
                {
                    Console.Write("Secret: ");
                    secret = Console.ReadLine();
                }

                AppInfo = new
                {
                    CLIENT_ID = id,
                    CLIENT_SECRET = secret
                };
                File.WriteAllText("AppAuth.json", JsonConvert.SerializeObject(AppInfo));
            }

            service = new TwitterService(AppInfo.CLIENT_ID, AppInfo.CLIENT_SECRET);
            access = null;
            if (File.Exists("token.json"))
            {
                access = service.Deserialize<OAuthAccessToken>(File.ReadAllText("token.json"));
            }
            if (access == null)
            {
                OAuthRequestToken requestToken = service.GetRequestToken();
                Uri uri = service.GetAuthorizationUri(requestToken);
                Console.WriteLine(uri.ToString());
                var verifier = Console.ReadLine();
                access = service.GetAccessToken(requestToken, verifier);
                var tokenstr = service.Serializer.Serialize(access, typeof(OAuthAccessToken));
                File.WriteAllText("token.json", tokenstr);
            }
            service.AuthenticateWith(access.Token, access.TokenSecret);

            Console.WriteLine($"Authenticated as {access.ScreenName}");

            AllTweets = new TwitterStatus[0];
            if (File.Exists("AllTweets.json"))
            {
                AllTweets =
                    JsonConvert.DeserializeObject<TwitterStatus[]>(File.ReadAllText("AllTweets.json"))
                    .Union(AllTweets)
                    .OrderBy(t => t.Id)
                    .ToArray();
            }
            History = new List<Match>();
            if (File.Exists("history.json"))
            {

                var blob = File.ReadAllText("history.json");
                var matches = JsonConvert.DeserializeObject<Match[]>(blob);
                History.AddRange(matches);
            }

            GetTweets();
            LoadRoundTimerFromDisk();
            Save();
        NextRound:
            Refresh();
            if (CurrentBout.Announcement == -1)
            {
                Announce();
                Save();
            }

            if (Announcement == null)
            {
                Announcement = service.GetTweet(new GetTweetOptions() { Id = CurrentBout.Announcement });
                if (Announcement == null)
                {
                    Announce();
                    Announcement = service.GetTweet(new GetTweetOptions() { Id = CurrentBout.Announcement });
                }
                Console.WriteLine($"> {Announcement.Text}");
            }
            var age = DateTime.UtcNow.Subtract(Announcement.CreatedDate);
            var TimeBetweenRounds = OneMinute; //new TimeSpan(RoundDuration.Ticks / 10);

            while (age.TotalSeconds < (RoundDuration.TotalSeconds - TimeBetweenRounds.TotalSeconds))
            {
                var diff = RoundDuration.Subtract(age).Subtract(TimeBetweenRounds);
                if (diff.TotalHours > 1)
                {
                    Console.WriteLine($" Sleeping 1 hour of {diff.TotalHours}");
                    Thread.Sleep(OneHour);
                    GetTweets();
                    if (DateTime.Now.Subtract(LastRecount).TotalHours > 2)
                    {
                        Recount();
                        LastRecount = DateTime.Now;
                    }
                }
                else
                {
                    Console.WriteLine($" Sleeping {diff.ToString()}");
                    Thread.Sleep(diff);
                }
                LoadRoundTimerFromDisk(); // In case it's been updated.
                age = DateTime.UtcNow.Subtract(Announcement.CreatedDate);
            }
            CalcWinner();
            Save();


            Console.WriteLine($" Sleeping {TimeBetweenRounds.ToString()}");
            Thread.Sleep(TimeBetweenRounds);
            if (DateTime.Now.Subtract(LastRecount).TotalHours > 12)
            {
                Recount();
                LastRecount = DateTime.Now;
            }
            if (RoundDuration.TotalMinutes >= 60 && DateTime.Now.Minute != 0)
            {
                Console.WriteLine($" Sleeping {OneMinute.ToString()}");
                Thread.Sleep(OneMinute);
            }

            goto NextRound;
        }

        private static async void Recount()
        {
            var match = History.Where(n => n.Winner == -2).OrderBy(n => n.LastRecounted).First();

            var PointsA = CountPoints(match.A);
            var PointsB = CountPoints(match.B);

            long winner = -1;
            if (await PointsA > await PointsB)
            {
                winner = match.A;
            }
            else if (await PointsB > await PointsA)
            {
                winner = match.B;
            }

            if (winner > 0)
            {
                match.Winner = winner;
                var status = $"Afer receiving a late vote, we've recounted, and found that Snow is {Lookup(winner).Text.Split(' ').Last()}!";
                Console.WriteLine(status);
                service.SendTweet(new SendTweetOptions() { InReplyToStatusId = match.Announcement, Status = status });
            }

            match.LastRecounted = DateTime.Now.Ticks;
        }

        private static void LoadRoundTimerFromDisk()
        {
            if (File.Exists("RoundTime"))
            {
                try
                {
                    RoundDuration = TimeSpan.Parse(File.ReadAllText("RoundTime"));
                }
                catch (Exception)
                {

                }
            }
        }

        private static void GetTweets()
        {
            if (AllTweets.Any())
            {
                // Get tweets that have gone up since we last looked.
                var tweets = service.ListTweetsOnUserTimeline(new ListTweetsOnUserTimelineOptions()
                {
                    ScreenName = "SnowIsEveryword",
                    Count = 1000,
                    TrimUser = true,
                    SinceId = AllTweets.Last().Id
                });
                // Get tweets older than we have in our records.  This will eventally dry up.
                tweets = tweets.Union(service.ListTweetsOnUserTimeline(new ListTweetsOnUserTimelineOptions()
                {
                    ScreenName = "SnowIsEveryword",
                    Count = 1000,
                    TrimUser = true,
                    MaxId = AllTweets.First().Id
                }));

                {
                    // Slowly Trawl back through older tweets, seeing if we missed some in the middle.
                    if (LatestRetrievedTweetThisSession < 1)
                        LatestRetrievedTweetThisSession = AllTweets.Last().Id;
                    var Regrab = service.ListTweetsOnUserTimeline(new ListTweetsOnUserTimelineOptions()
                    {
                        ScreenName = "SnowIsEveryword",
                        Count = 200,
                        TrimUser = true,
                        MaxId = LatestRetrievedTweetThisSession
                    });
                    if (Regrab.Any())
                    {
                        LatestRetrievedTweetThisSession = Regrab.LastOrDefault().Id;
                        tweets = tweets.Union(Regrab);
                    }
                }

                var oldnum = AllTweets.Count();
                AllTweets = tweets.Union(AllTweets)
                .OrderBy(t => t.Id)
                .ToArray();

                Console.WriteLine($"Tweets before Update: {oldnum}");
                Console.WriteLine($"Tweets after Update:  {AllTweets.Count()}");
                Console.WriteLine($"Diff: {AllTweets.Count() - oldnum}");

            }
            else
            {
                AllTweets = service.ListTweetsOnUserTimeline(new ListTweetsOnUserTimelineOptions() { ScreenName = "SnowIsEveryword", Count = 1000, TrimUser = true }).ToArray();
            }
        }

        private static async void CalcWinner()
        {
            var PointsA = CountPoints(CurrentBout.A);
            var PointsB = CountPoints(CurrentBout.B);

            long winner = -1;
//TODO: Vary tweets.
//TODO: (Will be difficult) Add tenses.
            if (await PointsA > await PointsB)
            {
                winner = CurrentBout.A;
            }
            else if (await PointsA < await PointsB)
            {
                winner = CurrentBout.B;
            }

            if (winner == -1)
            {
                CurrentBout.Winner = -2;
                var status = $"Was Snow {Lookup(CurrentBout.A).Text.Split(' ').Last()}, or {Lookup(CurrentBout.B).Text.Split(' ').Last()}? We just don't know";
                Console.WriteLine(status);
                service.SendTweet(new SendTweetOptions() { /* InReplyToStatusId = CurrentBout.Announcement, */ Status = status });
                //RoundDuration = RoundDuration.Add(new TimeSpan(0,5,0));
            }
            else
            {
                CurrentBout.Winner = winner;
                var status = $"We have a consensus!  Snow is definitely {Lookup(winner).Text.Split(' ').Last()}!";
                Console.WriteLine(status);
                service.SendTweet(new SendTweetOptions() { InReplyToStatusId = CurrentBout.Announcement, Status = status });
                //service.FavoriteTweet(new FavoriteTweetOptions() { Id = B.Id });
                //if (RoundDuration > TenMinutes)
                //    RoundDuration = RoundDuration.Subtract(new TimeSpan(0, 1, 0));
            }
        }

        private static async Task<int> CountPoints(long tweetId)
        {
            var tweet = await LookupAsync(tweetId);
            return tweet.FavoriteCount + tweet.RetweetCount;

            //int i = b.FavoriteCount;
            //var Retweets = service.Retweets(new RetweetsOptions() { Id = b.Id, Count = 100, TrimUser = false });
            //foreach (var rt in Retweets)
            //{
            //    if (rt.User.Id == access.UserId)
            //        break;
            //    i++;
            //}
            //return i;
        }

        private static TwitterStatus Lookup(long id)
        {
            var lookup = LookupAsync(id);
            lookup.Wait();
            return lookup.Result;
        }

        private static async Task<TwitterStatus> LookupAsync(long Id)
        {
            var tweet = AllTweets.SingleOrDefault(t => t.Id == CurrentBout.A);

            if (tweet == null || DateTime.Now.Subtract(tweet.RetrievedAt).TotalMinutes > 5)
            {
                var newtweet = await service.GetTweetAsync(new GetTweetOptions() { Id = Id, TrimUser = true });

                if (newtweet.Response.Error == null)
                {
                    AllTweets = new TwitterStatus[] { tweet = newtweet.Value }.Union(AllTweets).ToArray();
                }
            }

            return tweet;
        }

        private static void Save()
        {
            var savestr = service.Serializer.Serialize(History, typeof(Match[]));
            File.WriteAllText("history.json", savestr);
            savestr = JsonConvert.SerializeObject(AllTweets/*.Select(t => t.Id)*/, Formatting.Indented);
            File.WriteAllText("AllTweets.json", savestr);
        }

        private static void Announce()
        {
            var A = AllTweets.Single(t => t.Id == CurrentBout.A);
            var B = AllTweets.Single(t => t.Id == CurrentBout.B);
            UnRetweet(A);
            UnRetweet(B);
            string status = $"Which of these best describes Snow? {A.Text.Split(' ').Last()}, or {B.Text.Split(' ').Last()}?\nLike or Retweet your choice!";
            Console.WriteLine(status);
            Announcement = service.SendTweet(new SendTweetOptions() { Status = status });
            CurrentBout.Announcement = Announcement.Id;
            service.Retweet(new RetweetOptions() { Id = A.Id });
            service.Retweet(new RetweetOptions() { Id = B.Id });
        }

        private static void UnRetweet(TwitterStatus tweet)
        {
            if (!tweet.IsRetweeted)
                return;
            var rts = service.Retweets(new RetweetsOptions() { Id = tweet.Id, Count = 100 });
            var mine = rts.FirstOrDefault(t => t.User.Id == access.UserId);
            if (mine != null)
            {
                service.DeleteTweet(new DeleteTweetOptions() { Id = mine.Id });
            }
        }

        private static void Refresh()
        {
            var Eliminated = History.Select(m => m.Winner == -2 ? -2 : m.Winner == m.A ? m.B : m.A).Where(m => m != -2).ToArray();
            AvailableTweets = AllTweets.Where(t => !Eliminated.Contains(t.Id)).ToArray();
            Dictionary<int, List<TwitterStatus>> Rounds = new Dictionary<int, List<TwitterStatus>>();
            AvailableTweets = AvailableTweets.OrderBy(t => t.RetweetCount * 100 + rand.Next(0,99)).ToArray();
            foreach (var item in AvailableTweets)
            {
                var r = History.Count(m => m.A == item.Id || m.B == item.Id);
                if (!Rounds.ContainsKey(r))
                    Rounds[r] = new List<TwitterStatus>();
                Rounds[r].Add(item);
            }

            var duration = Time.LargestIntervalWithUnits(new TimeSpan(Rounds[Rounds.Keys.Min()].Count * RoundDuration.Ticks / 2));
            var description = "Trying to definitively define @SnowMcNally, @EverywordCup style. ☆ " +
                "Bot by @Silasary ☆ " + 
                $"Round {Rounds.Keys.Min() + 1} might complete in {duration} ☆ " +
                $"New Matchup every {RoundDuration.TotalHours.ToString("N0")} Hours";
            Console.WriteLine(description);
            service.UpdateProfile(new UpdateProfileOptions()
            {
                Description = description
            });
            
            if (History.Count > 0 && History.Last().Winner == -1)
            {
                CurrentBout = History.Last();
                return;
            }

            int i = 0;
            var max = Rounds.Keys.Max();
            while (i <= max)
            {
                if (Rounds.ContainsKey(i))
                {
                    //Rounds[i].OrderBy(t => t.RetweetCount);
                    CurrentBout = new Match(
                        A: Rounds[i].FirstOrDefault(t => DateTime.Now.Subtract(t.CreatedDate).TotalHours > 12).Id, 
                        B: Rounds[i].Last().Id);
                    History.Add(CurrentBout);
                    return;
                }
                i++;
            }
        }
    }
}
