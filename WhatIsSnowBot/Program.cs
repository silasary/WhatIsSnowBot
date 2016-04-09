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
    partial class Program
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

        static void Main(string[] args)
        {
            service = new TwitterService(CLIENT_ID, CLIENT_SECRET);
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

            var tweets = GetTweets();
            AllTweets = tweets.Union(AllTweets)
                .OrderBy(t => t.Id)
                .ToArray();
            LoadRoundTimerFromDisk();
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
                Console.WriteLine($"> {Announcement.Text}");
            }
            var age = DateTime.UtcNow.Subtract(Announcement.CreatedDate);
            while (age.TotalSeconds < RoundDuration.TotalSeconds)
            {
                var diff = RoundDuration.Subtract(age);
                if (diff.TotalHours > 1)
                {
                    Console.WriteLine($" Sleeping 1 hour of {diff.TotalHours}");
                    Thread.Sleep(OneHour);
                    AllTweets = GetTweets().Union(AllTweets)
                        .OrderBy(t => t.Id)
                        .ToArray();
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

            Thread.Sleep(new TimeSpan(0, 1, 0));

            //Debugger.Break();
            goto NextRound;
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

        private static IEnumerable<TwitterStatus> GetTweets()
        {
            //var AsyncResp = service.ListTweetsOnUserTimeline(
            //    new ListTweetsOnUserTimelineOptions() { ScreenName = "SnowIsEveryword", Count = 1000, TrimUser = true }, 
            //    (tweets, resp) =>
            //    {

            //    });
            if (AllTweets.Any())
            {
                var tweets = service.ListTweetsOnUserTimeline(new ListTweetsOnUserTimelineOptions()
                {
                    ScreenName = "SnowIsEveryword",
                    Count = 1000,
                    TrimUser = true,
                    SinceId = AllTweets.Last().Id
                });
                tweets = tweets.Union(service.ListTweetsOnUserTimeline(new ListTweetsOnUserTimelineOptions()
                {
                    ScreenName = "SnowIsEveryword",
                    Count = 1000,
                    TrimUser = true,
                    MaxId = AllTweets.First().Id
                }));
                return tweets;
                
            }
            else
            {
                return service.ListTweetsOnUserTimeline(new ListTweetsOnUserTimelineOptions() { ScreenName = "SnowIsEveryword", Count = 1000, TrimUser = true });
            }
        }

        private static async void CalcWinner()
        {

            Lookup(CurrentBout.A, CurrentBout.B);
            var A = AllTweets.Single(t => t.Id == CurrentBout.A);
            var B = AllTweets.Single(t => t.Id == CurrentBout.B);


            var PointsA = await CountPoints(A);
            var PointsB = await CountPoints(B);
//TODO: Vary tweets.
//TODO: Add tenses.
            if (PointsA == PointsB)
            {
                CurrentBout.Winner = -2;
                var status = $"Was Snow {A.Text.Split(' ').Last()}, or {B.Text.Split(' ').Last()}? We just don't know";
                Console.WriteLine(status);
                service.SendTweet(new SendTweetOptions() { InReplyToStatusId = CurrentBout.Announcement, Status = status });
                //RoundDuration = RoundDuration.Add(new TimeSpan(0,5,0));
            }
            else if (PointsA > PointsB)
            {
                CurrentBout.Winner = A.Id;
                var status = $"We have a consensus!  Snow is definitely {A.Text.Split(' ').Last()}!";
                Console.WriteLine(status);
                service.SendTweet(new SendTweetOptions() { InReplyToStatusId = CurrentBout.Announcement, Status = status });
                //service.FavoriteTweet(new FavoriteTweetOptions() { Id = A.Id });
                //if (RoundDuration > TenMinutes)
                //    RoundDuration = RoundDuration.Subtract(new TimeSpan(0, 1, 0));
            }
            else if (PointsA < PointsB)
            {
                CurrentBout.Winner = B.Id;
                var status = $"We have a consensus!  Snow is definitely {B.Text.Split(' ').Last()}!";
                Console.WriteLine(status);
                service.SendTweet(new SendTweetOptions() { InReplyToStatusId = CurrentBout.Announcement, Status = status });
                //service.FavoriteTweet(new FavoriteTweetOptions() { Id = B.Id });
                //if (RoundDuration > TenMinutes)
                //    RoundDuration = RoundDuration.Subtract(new TimeSpan(0, 1, 0));
            }
        }

        private static async Task<int> CountPoints(TwitterStatus b)
        {
            return b.FavoriteCount + b.RetweetCount;

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

        private static List<TwitterStatus> Lookup(params long[] Ids)
        {
            List<TwitterStatus> res = new List<TwitterStatus>();
            foreach (var item in Ids)
            {
                 res.Add(service.GetTweet(new GetTweetOptions() { Id = item, TrimUser = true }));
            }
            AllTweets = res.Union(AllTweets).ToArray();
            return res;
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
            foreach (var item in AvailableTweets)
            {
                var r = History.Count(m => m.A == item.Id || m.B == item.Id);
                if (!Rounds.ContainsKey(r))
                    Rounds[r] = new List<TwitterStatus>();
                Rounds[r].Add(item);
            }

            service.UpdateProfile(new UpdateProfileOptions()
            {
                Description = "Trying to definitively define @SnowMcNally, @EverywordCup style. ☆ " +
                "Bot by @Silasary ☆ " + 
                $"Round {Rounds.Keys.Min() + 1} might complete in {(Rounds[Rounds.Keys.Min()].Count * RoundDuration.TotalDays / 2).ToString("N1")} days ☆ " +
                $"New Matchup every {RoundDuration.TotalHours.ToString("N0")} Hours"
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
                    Rounds[i].OrderBy(t => t.RetweetCount);
                    //TODO: More randomness.
                    CurrentBout = new Match(A: Rounds[i].First().Id, B: Rounds[i].Last().Id); 
                    History.Add(CurrentBout);
                    return;
                }
                i++;
            }
            AvailableTweets.OrderBy(t => t.RetweetCount);
        }
    }
}
