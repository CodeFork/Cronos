﻿using System;
using System.Runtime.CompilerServices;

namespace Cronos
{
    /// <summary>
    /// Provides a parser and scheduler for cron expressions.
    /// </summary>
    public sealed class CronExpression
    {
        private const int MinDaysInMonth = 28;
        private const int MinNthDayOfWeek = 1;
        private const int MaxNthDayOfWeek = 5;
        private const int SundayBits = 0b1000_0001;

        // All possible last days of month: the 28th, the 29th, the 30th, the 31th.
        private const int LastDaysOfMonths = 0b1111 << MinDaysInMonth;

        private const int MaxDay = 1;
        private const int MaxMonth = 1;
        private const int MaxYear = 2100;

        private static readonly DateTime MaxDateTime = new DateTime(MaxYear, MaxMonth, MaxDay);
        private static readonly TimeZoneInfo UtcTimeZone = TimeZoneInfo.Utc;

        private static readonly CronExpression Yearly = Parse("0 0 1 1 * ");
        private static readonly CronExpression Weekly = Parse("0 0 * * 0 ");
        private static readonly CronExpression Monthly = Parse("0 0 1 * * ");
        private static readonly CronExpression Daily = Parse("0 0 * * * ");
        private static readonly CronExpression Hourly = Parse("0 * * * * ");
        private static readonly CronExpression Minutely = Parse("* * * * * ");
        private static readonly CronExpression Secondly = Parse("* * * * * *", CronFormat.IncludeSeconds);

        private static readonly int[] DeBruijnPositions =
        {
            0, 1, 2, 53, 3, 7, 54, 27,
            4, 38, 41, 8, 34, 55, 48, 28,
            62, 5, 39, 46, 44, 42, 22, 9,
            24, 35, 59, 56, 49, 18, 29, 11,
            63, 52, 6, 26, 37, 40, 33, 47,
            61, 45, 43, 21, 23, 58, 17, 10,
            51, 25, 36, 32, 60, 20, 57, 16,
            50, 31, 19, 15, 30, 14, 13, 12
        };

        internal TimeZoneInfo TestLocalZone;

        private long _second;     // 60 bits -> from 0 bit to 59 bit in Int64
        private long _minute;     // 60 bits -> from 0 bit to 59 bit in Int64
        private long _hour;       // 24 bits -> from 0 bit to 23 bit in Int64
        private long _dayOfMonth; // 31 bits -> from 1 bit to 31 bit in Int64
        private long _month;      // 12 bits -> from 1 bit to 12 bit in Int64
        private long _dayOfWeek;  // 8 bits  -> from 0 bit to 7  bit in Int64

        private int _nthdayOfWeek;
        private int _lastMonthOffset;

        private CronExpressionFlag _flags;

        private CronExpression()
        {
        }

        ///<summary>
        /// Constructs a new <see cref="CronExpression"/> based on the specified
        /// cron expression. It's supported expressions consisting of 5 fields:
        /// minute, hour, day of month, month, day of week. 
        /// If you want to parse non-standard cron expresions use <see cref="Parse(string, CronFormat)"/> with specified CronFields argument.
        /// See more: <a href="https://github.com/HangfireIO/Cronos">https://github.com/HangfireIO/Cronos</a>
        /// </summary>
        public static CronExpression Parse(string expression)
        {
            return Parse(expression, CronFormat.Standard);
        }

        ///<summary>
        /// Constructs a new <see cref="CronExpression"/> based on the specified
        /// cron expression. It's supported expressions consisting of 5 or 6 fields:
        /// second (optional), minute, hour, day of month, month, day of week. 
        /// See more: <a href="https://github.com/HangfireIO/Cronos">https://github.com/HangfireIO/Cronos</a>
        /// </summary>
        public static CronExpression Parse(string expression, CronFormat format)
        {
            if (string.IsNullOrEmpty(expression)) throw new ArgumentNullException(nameof(expression));

            unsafe
            {
                fixed (char* value = expression)
                {
                    var pointer = value;

                    SkipWhiteSpaces(ref pointer);

                    if (*pointer == '@')
                    {
                        var macroExpression = ParseMacro(ref pointer);
                        if (macroExpression == null) ThrowFormatException("Unexpected character '{0}' on position {1}.", *pointer, pointer - value);

                        pointer++;

                        SkipWhiteSpaces(ref pointer);

                        if (!IsEndOfString(*pointer)) ThrowFormatException("Unexpected character '{0}' on position {1}, end of string expected.", *pointer, pointer - value);

                        return macroExpression;
                    }

                    var cronExpression = new CronExpression();

                    if ((format & CronFormat.IncludeSeconds) != 0)
                    {
                        ParseField(CronField.Seconds, ref pointer, cronExpression, ref cronExpression._second);
                    }
                    else
                    {
                        SetBit(ref cronExpression._second, 0);
                    }

                    ParseField(CronField.Minutes, ref pointer, cronExpression, ref cronExpression._minute);

                    ParseField(CronField.Hours, ref pointer, cronExpression, ref cronExpression._hour);

                    ParseField(CronField.DaysOfMonth, ref pointer, cronExpression, ref cronExpression._dayOfMonth);

                    ParseField(CronField.Months, ref pointer, cronExpression, ref cronExpression._month);

                    if (*pointer == '?' && cronExpression.HasFlag(CronExpressionFlag.DayOfMonthQuestion))
                    {
                        ThrowFormatException(CronField.DaysOfWeek, "'?' is not supported.");
                    }

                    ParseField(CronField.DaysOfWeek, ref pointer, cronExpression, ref cronExpression._dayOfWeek);

                    if (!IsEndOfString(*pointer))
                    {
                        ThrowFormatException("Unexpected character '{0}' on position {1}, end of string expected. Please use the '{2}' argument to specify non-standard CRON fields.", *pointer, pointer - value, nameof(format));
                    }
                    
                    // Make sundays equivalent.
                    if ((cronExpression._dayOfWeek & SundayBits) != 0)
                    {
                        cronExpression._dayOfWeek |= SundayBits;
                    }

                    return cronExpression;
                }
            }
        }

        /// <summary>
        /// Calculates next occurrence starting with <paramref name="from"/> (optionally <paramref name="inclusive"/>).
        /// </summary>
        public DateTime? GetNextOccurrence(DateTime from, bool inclusive = false)
        {
            if (from.Kind == DateTimeKind.Unspecified) ThrowDateTimeKindIsUnspecifiedException(nameof(from));

            if (from.Kind == DateTimeKind.Local)
            {
                var localZone = GetLocalTimeZone();

                var localOccurrence = GetOccurenceByZonedTimes(from, localZone, inclusive);
                if (localOccurrence == null) return null;

                return DateTime.SpecifyKind(localOccurrence.Value.DateTime, DateTimeKind.Local);
            }

            var found = FindOccurence(from, MaxDateTime, inclusive);
            if (found == null) return null;

            return DateTime.SpecifyKind(found.Value, DateTimeKind.Utc);
        }

        /// <summary>
        /// Calculates next occurrence starting with <paramref name="fromUtc"/> (optionally <paramref name="inclusive"/>) in given <paramref name="zone"/>
        /// </summary>
        public DateTime? GetNextOccurrence(DateTime fromUtc, TimeZoneInfo zone, bool inclusive = false)
        {
            if (fromUtc.Kind != DateTimeKind.Utc) ThrowWrongDateTimeKindException(nameof(fromUtc));

            if (zone == UtcTimeZone)
            {
                var found = FindOccurence(fromUtc, MaxDateTime, inclusive);
                if (found == null) return null;

                return DateTime.SpecifyKind(found.Value, DateTimeKind.Utc);
            }

            var zonedStart = TimeZoneInfo.ConvertTime(fromUtc, zone);

            var occurrence = GetOccurenceByZonedTimes(zonedStart, zone, inclusive);
            return occurrence?.UtcDateTime;
        }

        /// <summary>
        /// Calculates next occurrence starting with <paramref name="from"/> (optionally <paramref name="inclusive"/>) in given <paramref name="zone"/>
        /// </summary>
        public DateTimeOffset? GetNextOccurrence(DateTimeOffset from, TimeZoneInfo zone, bool inclusive = false)
        {
            if (zone == UtcTimeZone)
            {
                var found = FindOccurence(from.UtcDateTime, MaxDateTime, inclusive);
                if (found == null) return null;

                return new DateTimeOffset(found.Value, TimeSpan.Zero);
            }

            var zonedStart = TimeZoneInfo.ConvertTime(from, zone);

            return GetOccurenceByZonedTimes(zonedStart, zone, inclusive);
        }

        private DateTimeOffset? GetOccurenceByZonedTimes(DateTimeOffset zonedStartInclusive, TimeZoneInfo zone, bool inclusive)
        {
            var startLocalDateTime = zonedStartInclusive.DateTime;

            if (TimeZoneHelper.IsAmbiguousTime(zone, startLocalDateTime))
            {
                var currentOffset = zonedStartInclusive.Offset;
                var lateOffset = zone.BaseUtcOffset;
               
                if (lateOffset != currentOffset)
                {
                    var earlyOffset = TimeZoneHelper.GetDstOffset(startLocalDateTime, zone);
                    var earlyIntervalLocalEnd = TimeZoneHelper.GetDstEnd(zone, startLocalDateTime, earlyOffset);

                    // Early period, try to find anything here.
                    var found = FindOccurence(startLocalDateTime, earlyIntervalLocalEnd.DateTime, inclusive);
                    if (found.HasValue) return new DateTimeOffset(found.Value, earlyOffset);

                    startLocalDateTime = TimeZoneHelper.GetStandartTimeStart(zone, startLocalDateTime, earlyOffset).DateTime;
                    inclusive = true;
                }

                // Skip late ambiguous interval.
                var ambiguousTimeEnd = TimeZoneHelper.GetAmbiguousTimeEnd(zone, startLocalDateTime);

                var abmiguousTimeLastInstant = ambiguousTimeEnd.DateTime.AddTicks(-1);

                var foundInLateInterval = FindOccurence(startLocalDateTime, abmiguousTimeLastInstant, inclusive);

                if (foundInLateInterval.HasValue && HasFlag(CronExpressionFlag.Interval))
                    return new DateTimeOffset(foundInLateInterval.Value, lateOffset);

                startLocalDateTime = ambiguousTimeEnd.DateTime;
            }

            var occurrence = FindOccurence(startLocalDateTime, MaxDateTime, inclusive);
            if (occurrence == null) return null;

            if (zone.IsInvalidTime(occurrence.Value))
            {
                var nextValidTime = TimeZoneHelper.GetDstStart(zone, occurrence.Value, zone.BaseUtcOffset);
                return nextValidTime;
            }

            if (TimeZoneHelper.IsAmbiguousTime(zone, occurrence.Value))
            {
                var earlyOffset = TimeZoneHelper.GetDstOffset(occurrence.Value, zone);
                return new DateTimeOffset(occurrence.Value, earlyOffset);
            }

            return new DateTimeOffset(occurrence.Value, zone.GetUtcOffset(occurrence.Value));
        }

        private DateTime? FindOccurence(DateTime startTime, DateTime endTime, bool startInclusive)
        {
            if (!startInclusive) startTime = CalendarHelper.AddMillisecond(startTime);

            var endSecond = 0;
            var endMinute = 0;
            var endHour = 0;
            var endDay = MaxDay;
            var endMonth = MaxMonth;
            var endYear = MaxYear;

            if (endTime < MaxDateTime)
            {
                CalendarHelper.FillDateTimeParts(
                    endTime,
                    out endSecond,
                    out endMinute,
                    out endHour,
                    out endDay,
                    out endMonth,
                    out endYear);
            }

            CalendarHelper.FillDateTimeParts(
                startTime,
                out int startSecond,
                out int startMinute,
                out int startHour,
                out int startDay,
                out int startMonth,
                out int startYear);

            var year = startYear;
            var month = startMonth;
            var day = startDay;
            var hour = startHour;
            var minute = startMinute;
            var second = startSecond;

            var minSecond = FindFirstSet(_second, CronField.Seconds.First, CronField.Seconds.Last);
            var minMinute = FindFirstSet(_minute, CronField.Minutes.First, CronField.Minutes.Last);
            var minHour = FindFirstSet(_hour, CronField.Hours.First, CronField.Hours.Last);
            var minDay = FindFirstSet(_dayOfMonth, CronField.DaysOfMonth.First, CronField.DaysOfMonth.Last);
            var minMonth = FindFirstSet(_month, CronField.Months.First, CronField.Months.Last);

            void Rollover(CronField field, bool increment = true)
            {
                if (field == CronField.Seconds)
                {
                    second = minSecond;
                    if (increment) minute++;
                }
                else if (field == CronField.Minutes)
                {
                    second = minSecond;
                    minute = minMinute;
                    if (increment) hour++;
                }
                else if (field == CronField.Hours)
                {
                    second = minSecond;
                    minute = minMinute;
                    hour = minHour;
                    if (increment) day++;
                }
                else if (field == CronField.DaysOfMonth)
                {
                    second = minSecond;
                    minute = minMinute;
                    hour = minHour;
                    day = minDay;
                    if (increment) month++;
                }
                else if (field == CronField.Months)
                {
                    second = minSecond;
                    minute = minMinute;
                    hour = minHour;
                    day = minDay;
                    month = minMonth;
                    if (increment) year++;
                }
            }

            void MoveToNextValue(CronField field, long fieldBits, ref int value)
            {
                var nextValue = FindFirstSet(fieldBits, value, field.Last);
                if (nextValue == value) return;

                if (nextValue == -1)
                {
                    Rollover(field);
                    return;
                }

                Rollover(field.Previous, false);
                value = nextValue;
            }

            bool IsBeyondEndDate()
            {
                return CalendarHelper.IsLessThan(
                    endYear, endMonth, endDay, endHour, endMinute, endSecond, 
                    year, month, day, hour, minute, second);
            }

            if (HasFlag(CronExpressionFlag.NearestWeekday))
            {
                // If start day is Sunday or Saturday we must search from saturday 00:00 am.
                // Next occurrence can be before startTime but not week day.
                // So we'll shift occurrence to Monday and result will be after startTime.
                if (startDay >= minDay && startDay <= minDay + 2)
                {
                    var dayOfWeek = CalendarHelper.GetDayOfWeek(startYear, startMonth, minDay);

                    if (dayOfWeek == DayOfWeek.Saturday || dayOfWeek == DayOfWeek.Sunday)
                    {
                        day = minDay;
                        Rollover(CronField.Hours, false);
                    }
                }
            }

            MoveToNextValue(CronField.Seconds, _second, ref second);
            MoveToNextValue(CronField.Minutes, _minute, ref minute);
            MoveToNextValue(CronField.Hours, _hour, ref hour);

            RetryDayOfMonth:

            MoveToNextValue(CronField.DaysOfMonth, _dayOfMonth, ref day);

            RetryMonth:

            MoveToNextValue(CronField.Months, _month, ref month);

            var lastDayOfMonth = CalendarHelper.GetDaysInMonth(year, month);

            if (day > lastDayOfMonth)
            {
                day = lastDayOfMonth;

                if (IsBeyondEndDate()) return null;

                Rollover(CronField.DaysOfMonth);
                goto RetryDayOfMonth;
            }

            if (HasFlag(CronExpressionFlag.DayOfMonthLast))
            {
                var lastDayMonthWithOffset = lastDayOfMonth - _lastMonthOffset;

                if (lastDayMonthWithOffset > day)
                {
                    Rollover(CronField.Hours, false);
                    day = lastDayMonthWithOffset;
                }
                else if (lastDayMonthWithOffset < day)
                {
                    if (IsBeyondEndDate()) return null;

                    Rollover(CronField.DaysOfMonth);
                    goto RetryMonth;
                }

                if (!IsDayOfWeekMatch(year, month, day))
                {
                    if (IsBeyondEndDate()) return null;

                    Rollover(CronField.Hours);
                    goto RetryDayOfMonth;
                }
            }

            // W character.

            if (HasFlag(CronExpressionFlag.NearestWeekday))
            {
                var dayOfWeek = CalendarHelper.GetDayOfWeek(year, month, day);
                var shift = CalendarHelper.MoveToNearestWeekDay(ref day, ref dayOfWeek, lastDayOfMonth);

                if (shift > 0)
                {
                    Rollover(CronField.Hours, false);
                }
                else if (shift < 0)
                {
                    if (CalendarHelper.IsLessThan(year, month, day, 0, 0, 0, startYear, startMonth, startDay, 0, 0, 0))
                    {
                        Rollover(CronField.DaysOfMonth);
                        goto RetryMonth;
                    }
                }

                if (year == startYear && month == startMonth && day == startDay)
                {
                    hour = startHour;
                    minute = startMinute;
                    second = startSecond;

                    MoveToNextValue(CronField.Seconds, _second, ref second);
                    MoveToNextValue(CronField.Minutes, _minute, ref minute);
                    MoveToNextValue(CronField.Hours, _hour, ref hour);

                    if (day == -1 || day != startDay)
                    {
                        Rollover(CronField.DaysOfMonth);
                        goto RetryMonth;
                    }
                }

                if (IsBeyondEndDate()) return null;

                if (!IsDayOfWeekMatch(dayOfWeek) ||
                    HasFlag(CronExpressionFlag.DayOfWeekLast) && !CalendarHelper.IsLastDayOfWeek(day, lastDayOfMonth) ||
                    HasFlag(CronExpressionFlag.NthDayOfWeek) && !CalendarHelper.IsNthDayOfWeek(day, _nthdayOfWeek))
                {
                    Rollover(CronField.DaysOfMonth);
                    goto RetryMonth;
                }
            }

            if (IsBeyondEndDate()) return null;

            // L and # characters in day of week.

            if (!IsDayOfWeekMatch(year, month, day) ||
                HasFlag(CronExpressionFlag.DayOfWeekLast) && !CalendarHelper.IsLastDayOfWeek(day, lastDayOfMonth) ||
                HasFlag(CronExpressionFlag.NthDayOfWeek) && !CalendarHelper.IsNthDayOfWeek(day, _nthdayOfWeek))
            {
                Rollover(CronField.Hours);
                goto RetryDayOfMonth;
            }

            return new DateTime(CalendarHelper.DateTimeToTicks(year, month, day, hour, minute, second));
        }

#if !NET40
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private bool IsDayOfWeekMatch(int year, int month, int day)
        {
            if (_dayOfWeek == -1L) return true;

            var dayOfWeek = CalendarHelper.GetDayOfWeek(year, month, day);
            return ((_dayOfWeek >> (int)dayOfWeek) & 1) != 0;
        }

#if !NET40
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private bool IsDayOfWeekMatch(DayOfWeek dayOfWeek)
        {
            return ((_dayOfWeek >> (int)dayOfWeek) & 1) != 0;
        }

#if !NET40
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private static int FindFirstSet(long value, int startBit, int endBit)
        {
            if (startBit <= endBit && GetBit(value, startBit)) return startBit;

            // TODO: Add description and source

            value = value >> startBit;
            if (value == 0) return -1;

            ulong res = unchecked((ulong)(value & -value) * 0x022fdd63cc95386d) >> 58;

            var result = DeBruijnPositions[res] + startBit;
            if (result > endBit) return -1;

            return result;
        }

#if !NET40
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private bool HasFlag(CronExpressionFlag value)
        {
            return (_flags & value) != 0;
        }

#if !NET40
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private TimeZoneInfo GetLocalTimeZone()
        {
            return TestLocalZone ?? TimeZoneInfo.Local;
        }

#if !NET40
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private static unsafe void SkipWhiteSpaces(ref char* pointer)
        {
            while (IsWhiteSpace(*pointer)) pointer++; 
        }

        private static unsafe CronExpression ParseMacro(ref char* pointer)
        {
            pointer++;

            switch (ToUpper(*pointer))
            {
                case 'A':
                    if (ToUpper(*++pointer) == 'N' &&
                        ToUpper(*++pointer) == 'N' &&
                        ToUpper(*++pointer) == 'U' &&
                        ToUpper(*++pointer) == 'A' &&
                        ToUpper(*++pointer) == 'L' &&
                        ToUpper(*++pointer) == 'L' &&
                        ToUpper(*++pointer) == 'Y')
                        return Yearly;
                    return null;
                case 'D':
                    if (ToUpper(*++pointer) == 'A' &&
                        ToUpper(*++pointer) == 'I' &&
                        ToUpper(*++pointer) == 'L' &&
                        ToUpper(*++pointer) == 'Y')
                        return Daily;
                    return null;
                case 'E':
                    if (ToUpper(*++pointer) == 'V' &&
                        ToUpper(*++pointer) == 'E' &&
                        ToUpper(*++pointer) == 'R' &&
                        ToUpper(*++pointer) == 'Y' &&
                        ToUpper(*++pointer) == '_')
                    {
                        pointer++;
                        if (ToUpper(*pointer) == 'M' &&
                            ToUpper(*++pointer) == 'I' &&
                            ToUpper(*++pointer) == 'N' &&
                            ToUpper(*++pointer) == 'U' &&
                            ToUpper(*++pointer) == 'T' &&
                            ToUpper(*++pointer) == 'E')
                            return Minutely;

                        if (*(pointer - 1) != '_') return null;

                        if (*(pointer - 1) == '_' &&
                            ToUpper(*pointer) == 'S' &&
                            ToUpper(*++pointer) == 'E' &&
                            ToUpper(*++pointer) == 'C' &&
                            ToUpper(*++pointer) == 'O' &&
                            ToUpper(*++pointer) == 'N' &&
                            ToUpper(*++pointer) == 'D')
                            return Secondly;
                    }

                    return null;
                case 'H':
                    if (ToUpper(*++pointer) == 'O' &&
                        ToUpper(*++pointer) == 'U' &&
                        ToUpper(*++pointer) == 'R' &&
                        ToUpper(*++pointer) == 'L' &&
                        ToUpper(*++pointer) == 'Y')
                        return Hourly;
                    return null;
                case 'M':
                    pointer++;
                    if (ToUpper(*pointer) == 'O' &&
                        ToUpper(*++pointer) == 'N' &&
                        ToUpper(*++pointer) == 'T' &&
                        ToUpper(*++pointer) == 'H' &&
                        ToUpper(*++pointer) == 'L' &&
                        ToUpper(*++pointer) == 'Y')
                        return Monthly;

                    if (ToUpper(*(pointer - 1)) == 'M' &&
                        ToUpper(*pointer) == 'I' &&
                        ToUpper(*++pointer) == 'D' &&
                        ToUpper(*++pointer) == 'N' &&
                        ToUpper(*++pointer) == 'I' &&
                        ToUpper(*++pointer) == 'G' &&
                        ToUpper(*++pointer) == 'H' &&
                        ToUpper(*++pointer) == 'T')
                        return Daily;

                    return null;
                case 'W':
                    if (ToUpper(*++pointer) == 'E' &&
                        ToUpper(*++pointer) == 'E' &&
                        ToUpper(*++pointer) == 'K' &&
                        ToUpper(*++pointer) == 'L' &&
                        ToUpper(*++pointer) == 'Y')
                        return Weekly;
                    return null;
                case 'Y':
                    if (ToUpper(*++pointer) == 'E' &&
                        ToUpper(*++pointer) == 'A' &&
                        ToUpper(*++pointer) == 'R' &&
                        ToUpper(*++pointer) == 'L' &&
                        ToUpper(*++pointer) == 'Y')
                        return Yearly;
                    return null;
                default:
                    return null;
            }
        }

        private static unsafe void ParseField(
            CronField field,
            ref char* pointer, 
            CronExpression expression, 
            ref long bits)
        {
            if (*pointer == '*')
            {
                pointer++;

                if (field.CanDefineInterval) expression._flags |= CronExpressionFlag.Interval;

                if (*pointer != '/')
                {
                    SetAllBits(out bits);

                    if(!IsWhiteSpace(*pointer) && !IsEndOfString(*pointer)) ThrowFormatException(field, "'{0}' is not supported after '*'.", *pointer);

                    SkipWhiteSpaces(ref pointer);

                    return;
                }

                ParseRange(field, ref pointer, expression, ref bits, true);
            }
            else
            {
                ParseList(field, ref pointer, expression, ref bits);
            }

            if (field == CronField.DaysOfMonth)
            {
                if (*pointer == 'W')
                {
                    pointer++;
                    expression._flags |= CronExpressionFlag.NearestWeekday;
                }
            }
            else if (field == CronField.DaysOfWeek)
            {
                if (*pointer == 'L')
                {
                    pointer++;
                    expression._flags |= CronExpressionFlag.DayOfWeekLast;
                }

                if (*pointer == '#')
                {
                    pointer++;
                    expression._flags |= CronExpressionFlag.NthDayOfWeek;
                    pointer = GetNumber(out expression._nthdayOfWeek, MinNthDayOfWeek, null, pointer);

                    if (pointer == null || expression._nthdayOfWeek < MinNthDayOfWeek || expression._nthdayOfWeek > MaxNthDayOfWeek)
                    {
                        ThrowFormatException(field, "'#' must be followed by a number between {0} and {1}.", MinNthDayOfWeek, MaxNthDayOfWeek);
                    }
                }
            }

            if (!IsWhiteSpace(*pointer) && !IsEndOfString(*pointer)) ThrowFormatException(field, "Unexpected character '{0}'.", *pointer);

            SkipWhiteSpaces(ref pointer);
        }

        private static unsafe void ParseList(
            CronField field, 
            ref char* pointer, 
            CronExpression expression, 
            ref long bits)
        {
            var singleValue = true;
            while (true)
            {
                ParseRange(field, ref pointer, expression, ref bits, false);

                if (*pointer == ',')
                {
                    singleValue = false;
                    pointer++;
                }
                else
                {
                    break;
                }
            }

            if (*pointer == 'W' && !singleValue)
            {
                ThrowFormatException(field, "Using some numbers with 'W' is not supported.");
            }
        }

        private static unsafe void ParseRange(
            CronField field, 
            ref char* pointer, 
            CronExpression expression, 
            ref long bits,
            bool star)
        {
            int num1, num2, num3;

            var low = field.First;
            var high = field.Last;

            if (star)
            {
                num1 = low;
                num2 = high;
            }
            else if(*pointer == '?')
            {
                if (field != CronField.DaysOfMonth && field != CronField.DaysOfWeek)
                {
                    ThrowFormatException(field, "'?' is not supported.");
                }

                pointer++;

                if (field == CronField.DaysOfMonth)
                {
                    expression._flags |= CronExpressionFlag.DayOfMonthQuestion;
                }

                if (*pointer == '/')
                {
                    ThrowFormatException(field, "'/' is not allowed after '?'.");
                }

                SetAllBits(out bits);

                return;
            }
            else if(*pointer == 'L')
            {
                if (field != CronField.DaysOfMonth)
                {
                    ThrowFormatException(field, "'L' is not supported.");
                }

                pointer++;

                bits = LastDaysOfMonths;

                expression._flags |= CronExpressionFlag.DayOfMonthLast;

                if (*pointer == '-')
                {
                    // Eat the dash.
                    pointer++;

                    // Get the number following the dash.
                    if ((pointer = GetNumber(out int lastMonthOffset, 0, null, pointer)) == null || lastMonthOffset < 0 || lastMonthOffset >= high)
                    {
                        ThrowFormatException(field, "Last month offset must be a number between {0} and {1} (all inclusive).", low, high);
                    }

                    bits = bits >> lastMonthOffset;
                    expression._lastMonthOffset = lastMonthOffset;
                }
                return;
            }
            else
            {
                var names = field.Names;

                if ((pointer = GetNumber(out num1, low, names, pointer)) == null || num1 < low || num1 > high)
                {
                    ThrowFormatException(field, "Value must be a number between {0} and {1} (all inclusive).", field, low, high);
                }

                if (*pointer == '-')
                {
                    if (field.CanDefineInterval) expression._flags |= CronExpressionFlag.Interval;

                    // Eat the dash.
                    pointer++;

                    // Get the number following the dash.
                    if ((pointer = GetNumber(out num2, low, names, pointer)) == null || num2 < low || num2 > high)
                    {
                        ThrowFormatException(field, "Range must contain numbers between {0} and {1} (all inclusive).", low, high);
                    }

                    if (*pointer == 'W')
                    {
                        ThrowFormatException(field, "'W' is not allowed after '-'.");
                    }
                }
                else if (*pointer == '/')
                {
                    if (field.CanDefineInterval) expression._flags |= CronExpressionFlag.Interval;

                    // If case of slash upper bound is high. E.g. '10/2` means 'every value from 10 to high with step size = 2'.
                    num2 = high;
                }
                else
                {
                    SetBit(ref bits, num1);
                    return;
                }
            }

            // Check for step size.
            if (*pointer == '/')
            {
                // Eat the slash.
                pointer++;

                // Get the step size -- note: we don't pass the
                // names here, because the number is not an
                // element id, it's a step size.  'low' is
                // sent as a 0 since there is no offset either.
                if ((pointer = GetNumber(out num3, 0, null, pointer)) == null || num3 <= 0 || num3 > high)
                {
                    ThrowFormatException(field, "Step must be a number between 1 and {0} (all inclusive).", high);
                }
                if (*pointer == 'W')
                {
                    ThrowFormatException(field, "'W' is not allowed after '/'.");
                }
            }
            else
            {
                // No step. Default == 1.
                num3 = 1;
            }

            // If upper bound less than bottom one, e.g. range 55-10 specified
            // we'll set bits from 0 to 15 then we shift it right by 5 bits.
            int shift = 0;
            if (num2 < num1)
            {
                // Skip one of sundays.
                if (field == CronField.DaysOfWeek) high--;

                shift = high - num1 + 1;
                num2 = num2 + shift;
                num1 = low;
            }

            // Range. set all elements from num1 to num2, stepping
            // by num3.
            if (num3 == 1 && num1 < num2 + 1)
            {
                // Fast path, to set all the required bits at once.
                bits |= (1L << (num2 + 1)) - (1L << num1);
            }
            else
            {
                for (var i = num1; i <= num2; i += num3)
                {
                    SetBit(ref bits, i);
                }
            }

            // If we have range like 55-10 or 11-1, so num2 > num1 we have to shift bits right.
            bits = shift == 0 
                ? bits 
                : bits >> shift | bits << (high - low - shift + 1);
        }

        private static unsafe char* GetNumber(
            out int num, /* where does the result go? */
            int low, /* offset applied to result if symbolic enum used */
            int[] names, /* symbolic names, if any, for enums */
            char* pointer)
        {
            num = 0;

            if (IsDigit(*pointer))
            {
                num = GetNumeric(*pointer++);

                if (!IsDigit(*pointer)) return pointer;

                num = num * 10 + GetNumeric(*pointer++);

                if (!IsDigit(*pointer)) return pointer;

                return null;
            }

            if (names == null) return null;

            if (!IsLetter(*pointer)) return null;
            var buffer = ToUpper(*pointer++);

            if (!IsLetter(*pointer)) return null;
            buffer |= ToUpper(*pointer++) << 8;

            if (!IsLetter(*pointer)) return null;
            buffer |= ToUpper(*pointer++) << 16;

            if (IsLetter(*pointer)) return null;

            var length = names.Length;

            for (var i = 0; i < length; i++)
            {
                if (buffer == names[i])
                {
                    num = i + low;
                    return pointer;
                }
            }

            return null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowFormatException(CronField field, string format, params object[] args)
        {
            throw new CronFormatException(field, String.Format(format, args));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowFormatException(string format, params object[] args)
        {
            throw new CronFormatException(String.Format(format, args));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowWrongDateTimeKindException(string paramName)
        {
            throw new ArgumentException("The supplied DateTime must have the Kind property set to Utc", paramName);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowDateTimeKindIsUnspecifiedException(string paramName)
        {
            throw new ArgumentException("The supplied DateTime must have the Kind property set to Utc or Local", paramName);
        }

#if !NET40
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private static bool GetBit(long value, int index)
        {
            return (value & (1L << index)) != 0;
        }

#if !NET40
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private static void SetBit(ref long value, int index)
        {
            value |= 1L << index;
        }

#if !NET40
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private static void SetAllBits(out long bits)
        {
            bits = -1L;
        }

#if !NET40
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private static bool IsEndOfString(int code)
        {
            return code == '\0';
        }

#if !NET40
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private static bool IsWhiteSpace(int code)
        {
            return code == '\t' || code == ' ';
        }

#if !NET40
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private static bool IsDigit(int code)
        {
            return code >= 48 && code <= 57;
        }

#if !NET40
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private static bool IsLetter(int code)
        {
            return (code >= 65 && code <= 90) || (code >= 97 && code <= 122);
        }

#if !NET40
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private static int GetNumeric(int code)
        {
            return code - 48;
        }

#if !NET40
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private static int ToUpper(int code)
        {
            if (code >= 97 && code <= 122)
            {
                return code - 32;
            }

            return code;
        }
    }
}