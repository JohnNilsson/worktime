using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Linq;

namespace WorkTime
{
  internal enum EventType
  {
    Locked = 4800,
    Unlocked = 4801
  }

  internal struct Event
  {
    public DateTime Time { get; set; }
    public EventType Type { get; set; }
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm} - {Type}";
  }

  internal struct Range
  {
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public TimeSpan Length => End - Start;

    public override string ToString() => $"{Start:yyyy-MM-dd HH:mm} - {Length:g}";

    public bool Intersects(Range other) => !(this.End < other.Start || this.Start > other.End);
  }


  public static class Program
  {
    public static void Main(params string[] args)
    {
      var from = DateTime.Today.AddDays(-40);
      var to = DateTime.Today.AddDays(1);
      var dates = Enumerable.Range(0, (to - from).Days).Select(d => from.AddDays(d));

      const int blocksPerDay = 24 * 2;
      var ticksPerBlock = TimeSpan.FromDays(1).Ticks / blocksPerDay;



      var workBlocks = QueryLockEvents(from, to)
        .ToRanges()
        .SplitByDate()
        .GroupBy(r => r.Start.Date, r => r.ToDateHistogram(blocksPerDay), (date, bits) => (date:date, bits:Or(bits)))
        .ToDictionary(g => g.date,g => g.bits);


      var q =
        from date in dates
        let dateBits = workBlocks.ContainsKey(date) ? workBlocks[date] : new BitArray(blocksPerDay)
        let workedBlocks = dateBits.Cast<bool>().Count(b=>b)
        let worktime = DateTime.MinValue.AddTicks(workedBlocks * ticksPerBlock)
        let ascii = dateBits.Render()
        select new { date, ascii, worktime }
        ;

      var cal = CultureInfo.InvariantCulture.Calendar;
      Console.WriteLine($"DATUM       DAG: {new string(' ', blocksPerDay)} --   TID");
      int? lastWeek = null;
      foreach (var r in q)
      {
        var thisWeek = cal.GetWeekOfYear(r.date, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        if (!lastWeek.HasValue)
        {
          lastWeek = thisWeek;
        }
        else if (thisWeek != lastWeek.Value)
        {
          lastWeek = thisWeek;
          Console.WriteLine();
        }

        if (r.date.DayOfWeek == DayOfWeek.Saturday || r.date.DayOfWeek == DayOfWeek.Sunday)
        {
          Console.ForegroundColor = ConsoleColor.DarkRed;
        }
        else
        {
          Console.ResetColor();
        }

        Console.WriteLine($"{r.date:yyyy-MMM-dd ddd}: {r.ascii} --   {r.worktime:t}");
      }

#if DEBUG
      Console.WriteLine("Press any key");
      Console.ReadKey();
#endif
    }



    private static EventLogQuery CreqteLockEventsQuery(DateTime from, DateTime to)
    {
      var query = $@"*[System[(EventID=4800 or EventID=4801) and TimeCreated[@SystemTime >= '{from.ToUniversalTime():s}Z' and @SystemTime <= '{to.ToUniversalTime():s}Z']]]";
      return new EventLogQuery("Security",PathType.LogName,query);
    }



    private static IEnumerable<Event> QueryLockEvents(DateTime from, DateTime to)
    {

      var query = CreqteLockEventsQuery(from, to);
      using (var reader = new EventLogReader(query) { BatchSize = (to - from).Days })
      {
        var e = reader.ReadEvent();
        while (e != null)
        {
          yield return new Event
          {
            Type = (EventType)e.Id,
            Time = e.TimeCreated.Value.AddHours(-6)
          };
          e = reader.ReadEvent();
        }
      }
    }



    private static IEnumerable<Range> ToRanges(this IEnumerable<Event> events)
    {
      var started = DateTime.MinValue;
      foreach (var e in events.SkipWhile(_ => _.Type == EventType.Locked))
      {
        switch (e.Type)
        {
          case EventType.Unlocked:
            started = e.Time;
            break;
          case EventType.Locked:
            yield return new Range {Start = started, End = e.Time};
            break;
          default:
            throw new NotSupportedException();
        }
      }
    }



    private static IEnumerable<Range> SplitByDate(this IEnumerable<Range> ranges)
    {
      foreach (var range in ranges)
      {
        var start = range.Start;
        var endDate = range.End.Date;
        while (start < endDate)
        {
          var nextDate = start.Date.AddDays(1);
          yield return new Range {Start = start, End = nextDate.AddTicks(-1)};
          start = nextDate;
        }
        yield return new Range {Start = start, End = range.End};
      }
    }



    private static BitArray ToDateHistogram(this Range range, int buckets)
    {
      var date = range.Start.Date;
      var bucketTicks = TimeSpan.FromDays(1).Ticks / buckets;

      var startBucket = (range.Start.Ticks - date.Ticks) / bucketTicks;
      var endBucket   = Math.Min(buckets-1, Math.Ceiling( (range.End.Ticks   - date.Ticks) / (double)bucketTicks));

      var bits = new BitArray(buckets);

      for (var i = startBucket; i <= endBucket; i++)
      {
        bits.Set((int)i,true);
      }

      return bits;
    }

    private static BitArray Or(BitArray x, BitArray y) => x.Or(y);

    private static BitArray Or(IEnumerable<BitArray> arrays) => arrays.Aggregate(Or);


    private static string Render(this BitArray histogram)
    {
      var chars = new char[histogram.Length];
      for (var i = 0; i < histogram.Length; i++)
      {
        chars[i] = histogram.Get(i) ? '*' : '.';
      }
      return new string(chars);
    }



    private static DateTime Floor(DateTime dateTime, TimeSpan interval)
    {
      return dateTime.AddTicks(-(dateTime.Ticks % interval.Ticks));
    }

    private static DateTime Ceiling(DateTime dateTime, TimeSpan interval)
    {
      var overflow = dateTime.Ticks % interval.Ticks;

      return overflow == 0 ? dateTime : dateTime.AddTicks(interval.Ticks - overflow);
    }

    public static int GetIso8601WeekOfYear(DateTime time)
    {
      var cal = CultureInfo.InvariantCulture.Calendar;
      var day = cal.GetDayOfWeek(time);
      if (day >= DayOfWeek.Monday && day <= DayOfWeek.Wednesday)
      {
        time = time.AddDays(3);
      }

      // Return the week of our adjusted day
      return cal.GetWeekOfYear(time, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
    }
  }
}
