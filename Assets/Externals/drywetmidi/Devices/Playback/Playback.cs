﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Smf;
using Melanchall.DryWetMidi.Smf.Interaction;

namespace Melanchall.DryWetMidi.Devices
{
    /// <summary>
    /// Provides a way to play MIDI data through the specified output MIDI device.
    /// </summary>
    public sealed class Playback : IDisposable
    {
        #region Constants

        private const uint ClockInterval = 1; // ms

        #endregion

        #region Events

        /// <summary>
        /// Occurs when playback started via <see cref="Start"/> or <see cref="Play"/> methods.
        /// </summary>
        public event EventHandler Started;

        /// <summary>
        /// Occurs when playback stopped via <see cref="Stop"/> method.
        /// </summary>
        public event EventHandler Stopped;

        /// <summary>
        /// Occurs when playback finished, i.e. last event has been played and no
        /// need to restart playback due to value of the <see cref="Loop"/>.
        /// </summary>
        public event EventHandler Finished;

        /// <summary>
        /// Occurs when notes started to play. It will raised if playback's cursor
        /// gets in to notes.
        /// </summary>
        public event EventHandler<NotesEventArgs> NotesPlaybackStarted;

        /// <summary>
        /// Occurs when notes finished to play. It will raised if playback's cursor
        /// gets out from notes.
        /// </summary>
        public event EventHandler<NotesEventArgs> NotesPlaybackFinished;

        #endregion

        #region Fields

        private readonly IEnumerator<PlaybackEvent> _eventsEnumerator;
        private readonly TimeSpan _duration;
        private readonly long _durationInTicks;

        private readonly MidiClock _clock;

        private readonly HashSet<NotePlaybackEventMetadata> _activeNotesMetadata = new HashSet<NotePlaybackEventMetadata>();
        private readonly List<NotePlaybackEventMetadata> _notesMetadata;

        private bool _disposed = false;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="Playback"/> with the specified
        /// collection of MIDI events and tempo map.
        /// </summary>
        /// <param name="events">Collection of MIDI events to play.</param>
        /// <param name="tempoMap">Tempo map used to calculate events times.</param>
        /// <exception cref="ArgumentNullException"><paramref name="events"/> is null. -or-
        /// <paramref name="tempoMap"/> is null.</exception>
        public Playback(IEnumerable<MidiEvent> events, TempoMap tempoMap)
            : this(new[] { events }, tempoMap)
        {
            ThrowIfArgument.IsNull(nameof(events), events);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Playback"/> with the specified
        /// collection of MIDI events, tempo map and output MIDI device to play events through.
        /// </summary>
        /// <param name="events">Collection of MIDI events to play.</param>
        /// <param name="tempoMap">Tempo map used to calculate events times.</param>
        /// <param name="outputDevice">Output MIDI device to play <paramref name="events"/> through.</param>
        /// <exception cref="ArgumentNullException"><paramref name="events"/> is null. -or-
        /// <paramref name="tempoMap"/> is null. -or- <paramref name="outputDevice"/> is null.</exception>
        public Playback(IEnumerable<MidiEvent> events, TempoMap tempoMap, OutputDevice outputDevice)
            : this(new[] { events }, tempoMap, outputDevice)
        {
            ThrowIfArgument.IsNull(nameof(events), events);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Playback"/> with the specified
        /// collection of MIDI events collections and tempo map.
        /// </summary>
        /// <param name="events">Collection of MIDI events collections to play.</param>
        /// <param name="tempoMap">Tempo map used to calculate events times.</param>
        /// <exception cref="ArgumentNullException"><paramref name="events"/> is null. -or-
        /// <paramref name="tempoMap"/> is null.</exception>
        public Playback(IEnumerable<IEnumerable<MidiEvent>> events, TempoMap tempoMap)
            : this(GetTimedObjects(events), tempoMap)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Playback"/> with the specified
        /// collection of MIDI events collections, tempo map and output MIDI device to play events through.
        /// </summary>
        /// <param name="events">Collection of MIDI events collections to play.</param>
        /// <param name="tempoMap">Tempo map used to calculate events times.</param>
        /// <param name="outputDevice">Output MIDI device to play <paramref name="events"/> through.</param>
        /// <exception cref="ArgumentNullException"><paramref name="events"/> is null. -or-
        /// <paramref name="tempoMap"/> is null. -or- <paramref name="outputDevice"/> is null.</exception>
        public Playback(IEnumerable<IEnumerable<MidiEvent>> events, TempoMap tempoMap, OutputDevice outputDevice)
            : this(GetTimedObjects(events), tempoMap, outputDevice)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Playback"/> with the specified
        /// collection of timed objects and tempo map.
        /// </summary>
        /// <param name="timedObjects">Collection of timed objects to play.</param>
        /// <param name="tempoMap">Tempo map used to calculate events times.</param>
        /// <exception cref="ArgumentNullException"><paramref name="timedObjects"/> is null. -or-
        /// <paramref name="tempoMap"/> is null.</exception>
        public Playback(IEnumerable<ITimedObject> timedObjects, TempoMap tempoMap)
        {
            ThrowIfArgument.IsNull(nameof(timedObjects), timedObjects);
            ThrowIfArgument.IsNull(nameof(tempoMap), tempoMap);

            var playbackEvents = GetPlaybackEvents(timedObjects, tempoMap);
            _eventsEnumerator = playbackEvents.GetEnumerator();
            _eventsEnumerator.MoveNext();

            var lastPlaybackEvent = playbackEvents.LastOrDefault();
            _duration = lastPlaybackEvent?.Time ?? TimeSpan.Zero;
            _durationInTicks = lastPlaybackEvent?.RawTime ?? 0;

            _notesMetadata = playbackEvents.Select(e => e.Metadata.Note).Where(m => m != null).ToList();
            _notesMetadata.Sort((m1, m2) => m1.StartTime.CompareTo(m2.StartTime));

            TempoMap = tempoMap;

            _clock = new MidiClock(ClockInterval);
            _clock.Tick += OnClockTick;

            Snapping = new PlaybackSnapping(playbackEvents, tempoMap);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Playback"/> with the specified
        /// collection of timed objects, tempo map and output MIDI device to play events through.
        /// </summary>
        /// <param name="timedObjects">Collection of timed objects to play.</param>
        /// <param name="tempoMap">Tempo map used to calculate events times.</param>
        /// <param name="outputDevice">Output MIDI device to play <paramref name="timedObjects"/> through.</param>
        /// <exception cref="ArgumentNullException"><paramref name="timedObjects"/> is null. -or-
        /// <paramref name="tempoMap"/> is null. -or- <paramref name="outputDevice"/> is null.</exception>
        public Playback(IEnumerable<ITimedObject> timedObjects, TempoMap tempoMap, OutputDevice outputDevice)
            : this(timedObjects, tempoMap)
        {
            ThrowIfArgument.IsNull(nameof(outputDevice), outputDevice);

            OutputDevice = outputDevice;
        }

        #endregion

        #region Finalizer

        /// <summary>
        /// Finalizes the current instance of the <see cref="Playback"/>.
        /// </summary>
        ~Playback()
        {
            Dispose(false);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the tempo map used to calculate events times.
        /// </summary>
        public TempoMap TempoMap { get; }

        /// <summary>
        /// Gets or sets the output MIDI device to play MIDI data through.
        /// </summary>
        public OutputDevice OutputDevice { get; set; }

        /// <summary>
        /// Gets a value indicating whether playing is currently running or not.
        /// </summary>
        public bool IsRunning => _clock.IsRunning;

        /// <summary>
        /// Gets or sets a value indicating whether playing should automatically start
        /// from the first event after the last one played.
        /// </summary>
        public bool Loop { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether currently playing notes must be stopped
        /// on playback stop or not.
        /// </summary>
        public bool InterruptNotesOnStop { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether notes must be tracked or not. If false, notes
        /// will be treated as just Note On/Note Off events.
        /// </summary>
        public bool TrackNotes { get; set; }

        /// <summary>
        /// Gets or sets the speed of events playing. 1 means normal speed. For example, to play
        /// events twice slower this property should be set to 0.5.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is zero or negative.</exception>
        /// <exception cref="ObjectDisposedException">The current <see cref="Playback"/> is disposed.</exception>
        public double Speed
        {
            get { return _clock.Speed; }
            set
            {
                ThrowIfArgument.IsNonpositive(nameof(value), value, "Speed is zero or negative.");
                EnsureIsNotDisposed();

                _clock.Speed = value;
            }
        }

        /// <summary>
        /// Gets an object to manage playback's snap points.
        /// </summary>
        public PlaybackSnapping Snapping { get; }

        public NoteCallback NoteCallback { get; set; }

        public EventCallback EventCallback { get; set; }

        #endregion

        #region Methods

        /// <summary>
        /// Retrieves the duration of the playback in the specified format.
        /// </summary>
        /// <param name="durationType">Type that will represent the duration.</param>
        /// <returns>The duration of the playback as an instance of time span defined by
        /// <paramref name="durationType"/>.</returns>
        /// <exception cref="InvalidEnumArgumentException"><paramref name="durationType"/>
        /// specified an invalid value.</exception>
        public ITimeSpan GetDuration(TimeSpanType durationType)
        {
            ThrowIfArgument.IsInvalidEnumValue(nameof(durationType), durationType);

            return TimeConverter.ConvertTo((MetricTimeSpan)_duration, durationType, TempoMap);
        }

        /// <summary>
        /// Retrieves the duration of the playback in the specified format.
        /// </summary>
        /// <typeparam name="TTimeSpan">Type that will represent the duration.</typeparam>
        /// <returns>The duration of the playback as an instance of
        /// <typeparamref name="TTimeSpan"/>.</returns>
        public TTimeSpan GetDuration<TTimeSpan>()
            where TTimeSpan : ITimeSpan
        {
            return TimeConverter.ConvertTo<TTimeSpan>((MetricTimeSpan)_duration, TempoMap);
        }

        /// <summary>
        /// Retrieves the current time of the playback in the specified format.
        /// </summary>
        /// <param name="timeType">Type that will represent the current time.</param>
        /// <returns>The current time of the playback as an instance of time span defined by
        /// <paramref name="timeType"/>.</returns>
        /// <exception cref="InvalidEnumArgumentException"><paramref name="timeType"/> specified an invalid value.</exception>
        public ITimeSpan GetCurrentTime(TimeSpanType timeType)
        {
            ThrowIfArgument.IsInvalidEnumValue(nameof(timeType), timeType);

            return TimeConverter.ConvertTo((MetricTimeSpan)_clock.CurrentTime, timeType, TempoMap);
        }

        /// <summary>
        /// Retrieves the current time of the playback in the specified format.
        /// </summary>
        /// <typeparam name="TTimeSpan">Type that will represent the current time.</typeparam>
        /// <returns>The current time of the playback as an instance of
        /// <typeparamref name="TTimeSpan"/>.</returns>
        public TTimeSpan GetCurrentTime<TTimeSpan>()
            where TTimeSpan : ITimeSpan
        {
            return TimeConverter.ConvertTo<TTimeSpan>((MetricTimeSpan)_clock.CurrentTime, TempoMap);
        }

        /// <summary>
        /// Starts playing of the MIDI data. This method is non-blocking.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The current <see cref="Playback"/> is disposed.</exception>
        /// <exception cref="MidiDeviceException">An error occurred on device.</exception>
        public void Start()
        {
            EnsureIsNotDisposed();

            if (_clock.IsRunning)
                return;

            OutputDevice?.PrepareForEventsSending();
            StopStartNotes();
            _clock.Start();

            OnStarted();
        }

        /// <summary>
        /// Stops playing of the MIDI data. Note that this method doesn't reset playback position. If you
        /// call <see cref="Start"/>, playing will be resumed from the point where <see cref="Stop"/> was called.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The current <see cref="Playback"/> is disposed.</exception>
        /// <exception cref="MidiDeviceException">An error occurred on device.</exception>
        public void Stop()
        {
            EnsureIsNotDisposed();

            if (!IsRunning)
                return;

            _clock.Stop();

            if (InterruptNotesOnStop)
            {
                var currentTime = _clock.CurrentTime;

                Note note;
                var notes = new List<Note>();

                foreach (var noteMetadata in _activeNotesMetadata.ToArray())
                {
                    TryPlayNoteEvent(noteMetadata, false, currentTime, out note);
                    notes.Add(note);
                }

                OnNotesPlaybackFinished(notes.ToArray());

                _activeNotesMetadata.Clear();
            }

            OnStopped();
        }

        /// <summary>
        /// Starts playing of the MIDI data. This method will block execution of a program until all
        /// MIDI data is played.
        /// </summary>
        /// <remarks>
        /// If <see cref="Loop"/> is set to true, this method will execute forever.
        /// </remarks>
        /// <exception cref="ObjectDisposedException">The current <see cref="Playback"/> is disposed.</exception>
        /// <exception cref="MidiDeviceException">An error occurred on device.</exception>
        public void Play()
        {
            EnsureIsNotDisposed();

            Start();
            SpinWait.SpinUntil(() => !_clock.IsRunning);
        }

        /// <summary>
        /// Sets playback position to the time of the specified snap point.
        /// </summary>
        /// <param name="snapPoint">Snap point to move to.</param>
        /// <exception cref="ArgumentNullException"><paramref name="snapPoint"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">The current <see cref="Playback"/> is disposed.</exception>
        /// <exception cref="MidiDeviceException">An error occurred on device.</exception>
        public void MoveToSnapPoint(SnapPoint snapPoint)
        {
            ThrowIfArgument.IsNull(nameof(snapPoint), snapPoint);
            EnsureIsNotDisposed();

            if (!snapPoint.IsEnabled)
                return;

            MoveToTime((MetricTimeSpan)snapPoint.Time);
        }

        /// <summary>
        /// Sets playback position to the time of the previous snap point (relative to the current
        /// time of playback) that belongs to the specified <see cref="SnapPointsGroup"/>.
        /// </summary>
        /// <param name="snapPointsGroup"><see cref="SnapPointsGroup"/> that defines snap points to
        /// select the one from.</param>
        /// <exception cref="ArgumentNullException"><paramref name="snapPointsGroup"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">The current <see cref="Playback"/> is disposed.</exception>
        /// <exception cref="MidiDeviceException">An error occurred on device.</exception>
        public void MoveToPreviousSnapPoint(SnapPointsGroup snapPointsGroup)
        {
            ThrowIfArgument.IsNull(nameof(snapPointsGroup), snapPointsGroup);
            EnsureIsNotDisposed();

            var snapPoint = Snapping.GetPreviousSnapPoint(_clock.CurrentTime, snapPointsGroup);
            if (snapPoint != null)
                MoveToTime((MetricTimeSpan)snapPoint.Time);
        }

        /// <summary>
        /// Sets playback position to the time of the previous snap point (relative to the current
        /// time of playback).
        /// </summary>
        /// <exception cref="ObjectDisposedException">The current <see cref="Playback"/> is disposed.</exception>
        /// <exception cref="MidiDeviceException">An error occurred on device.</exception>
        public void MoveToPreviousSnapPoint()
        {
            EnsureIsNotDisposed();

            var snapPoint = Snapping.GetPreviousSnapPoint(_clock.CurrentTime);
            if (snapPoint != null)
                MoveToTime((MetricTimeSpan)snapPoint.Time);
        }

        /// <summary>
        /// Sets playback position to the time of the next snap point (relative to the current
        /// time of playback) that belongs to the specified <see cref="SnapPointsGroup"/>.
        /// </summary>
        /// <param name="snapPointsGroup"><see cref="SnapPointsGroup"/> that defines snap points to
        /// select the one from.</param>
        /// <exception cref="ArgumentNullException"><paramref name="snapPointsGroup"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">The current <see cref="Playback"/> is disposed.</exception>
        /// <exception cref="MidiDeviceException">An error occurred on device.</exception>
        public void MoveToNextSnapPoint(SnapPointsGroup snapPointsGroup)
        {
            ThrowIfArgument.IsNull(nameof(snapPointsGroup), snapPointsGroup);
            EnsureIsNotDisposed();

            var snapPoint = Snapping.GetNextSnapPoint(_clock.CurrentTime, snapPointsGroup);
            if (snapPoint != null)
                MoveToTime((MetricTimeSpan)snapPoint.Time);
        }

        /// <summary>
        /// Sets playback position to the time of the next snap point (relative to the current
        /// time of playback).
        /// </summary>
        /// <exception cref="ObjectDisposedException">The current <see cref="Playback"/> is disposed.</exception>
        /// <exception cref="MidiDeviceException">An error occurred on device.</exception>
        public void MoveToNextSnapPoint()
        {
            EnsureIsNotDisposed();

            var snapPoint = Snapping.GetNextSnapPoint(_clock.CurrentTime);
            if (snapPoint != null)
                MoveToTime((MetricTimeSpan)snapPoint.Time);
        }

        /// <summary>
        /// Sets playback position to the beginning of the MIDI data.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The current <see cref="Playback"/> is disposed.</exception>
        /// <exception cref="MidiDeviceException">An error occurred on device.</exception>
        public void MoveToStart()
        {
            EnsureIsNotDisposed();

            MoveToTime(new MetricTimeSpan());
        }

        /// <summary>
        /// Sets playback position to the specified time from the beginning of the MIDI data.
        /// </summary>
        /// <param name="time">Time from the beginning of the MIDI data to set playback position to.</param>
        /// <exception cref="ArgumentNullException"><paramref name="time"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">The current <see cref="Playback"/> is disposed.</exception>
        /// <exception cref="MidiDeviceException">An error occurred on device.</exception>
        public void MoveToTime(ITimeSpan time)
        {
            ThrowIfArgument.IsNull(nameof(time), time);
            EnsureIsNotDisposed();

            if (TimeConverter.ConvertFrom(time, TempoMap) > _durationInTicks)
                time = (MetricTimeSpan)_duration;

            var isRunning = IsRunning;

            _clock.Reset();
            SetStartTime(time);

            if (isRunning)
            {
                StopStartNotes();
                _clock.Start();
            }
        }

        /// <summary>
        /// Shifts playback position forward by the specified step.
        /// </summary>
        /// <param name="step">Amount of time to shift playback position by.</param>
        /// <exception cref="ArgumentNullException"><paramref name="step"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">The current <see cref="Playback"/> is disposed.</exception>
        /// <exception cref="MidiDeviceException">An error occurred on device.</exception>
        public void MoveForward(ITimeSpan step)
        {
            ThrowIfArgument.IsNull(nameof(step), step);
            EnsureIsNotDisposed();

            var currentTime = (MetricTimeSpan)_clock.CurrentTime;
            MoveToTime(currentTime.Add(step, TimeSpanMode.TimeLength));
        }

        /// <summary>
        /// Shifts playback position back by the specified step.
        /// </summary>
        /// <param name="step">Amount of time to shift playback position by.</param>
        /// <exception cref="ArgumentNullException"><paramref name="step"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">The current <see cref="Playback"/> is disposed.</exception>
        /// <exception cref="MidiDeviceException">An error occurred on device.</exception>
        public void MoveBack(ITimeSpan step)
        {
            ThrowIfArgument.IsNull(nameof(step), step);
            EnsureIsNotDisposed();

            var currentTime = (MetricTimeSpan)_clock.CurrentTime;
            MoveToTime(TimeConverter.ConvertTo<MetricTimeSpan>(step, TempoMap) > currentTime
                ? new MetricTimeSpan()
                : currentTime.Subtract(step, TimeSpanMode.TimeLength));
        }

        private void StopStartNotes()
        {
            if (!TrackNotes)
                return;

            var currentTime = _clock.CurrentTime;
            var notesToPlay = _notesMetadata.SkipWhile(m => m.EndTime <= currentTime)
                                            .TakeWhile(m => m.StartTime < currentTime)
                                            .Where(m => m.StartTime < currentTime && m.EndTime > currentTime)
                                            .Distinct()
                                            .ToArray();
            var onNotesMetadata = notesToPlay.Where(n => !_activeNotesMetadata.Contains(n)).ToArray();
            var offNotesMetadata = _activeNotesMetadata.Where(n => !notesToPlay.Contains(n)).ToArray();

            OutputDevice?.PrepareForEventsSending();

            Note note;
            var notes = new List<Note>();

            foreach (var noteMetadata in offNotesMetadata)
            {
                TryPlayNoteEvent(noteMetadata, false, currentTime, out note);
                notes.Add(note);
            }

            OnNotesPlaybackFinished(notes.ToArray());
            notes.Clear();

            foreach (var noteMetadata in onNotesMetadata)
            {
                TryPlayNoteEvent(noteMetadata, true, currentTime, out note);
                notes.Add(note);
            }

            OnNotesPlaybackStarted(notes.ToArray());
        }

        private void OnStarted()
        {
            Started?.Invoke(this, EventArgs.Empty);
        }

        private void OnStopped()
        {
            Stopped?.Invoke(this, EventArgs.Empty);
        }

        private void OnFinished()
        {
            Finished?.Invoke(this, EventArgs.Empty);
        }

        private void OnNotesPlaybackStarted(params Note[] notes)
        {
            NotesPlaybackStarted?.Invoke(this, new NotesEventArgs(notes));
        }

        private void OnNotesPlaybackFinished(params Note[] notes)
        {
            NotesPlaybackFinished?.Invoke(this, new NotesEventArgs(notes));
        }

        private void OnClockTick(object sender, TickEventArgs e)
        {
            var time = e.Time;

            do
            {
                var playbackEvent = _eventsEnumerator.Current;
                if (playbackEvent == null)
                    continue;

                if (playbackEvent.Time > time)
                    return;

                var midiEvent = playbackEvent.Event;
                if (midiEvent == null)
                    continue;

                if (!IsRunning)
                    return;

                Note note;
                if (TryPlayNoteEvent(playbackEvent, out note))
                {
                    if (note != null)
                    {
                        if (playbackEvent.Event is NoteOnEvent)
                            OnNotesPlaybackStarted(note);
                        else
                            OnNotesPlaybackFinished(note);
                    }

                    continue;
                }

                var eventCallback = EventCallback;
                if (eventCallback != null)
                    midiEvent = eventCallback(midiEvent.Clone(), playbackEvent.RawTime, time);

                if (midiEvent == null)
                    continue;

                SendEvent(midiEvent);
            }
            while (_eventsEnumerator.MoveNext());

            if (!Loop)
            {
                _clock.Stop();
                OnFinished();
                return;
            }

            _clock.Stop();
            _clock.Reset();
            _eventsEnumerator.Reset();
            _eventsEnumerator.MoveNext();
            _clock.Start();
        }

        private void EnsureIsNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException("Playback is disposed.");
        }

        private void SetStartTime(ITimeSpan time)
        {
            _clock.StartTime = _clock.CurrentTime = TimeConverter.ConvertTo<MetricTimeSpan>(time, TempoMap);

            _eventsEnumerator.Reset();
            do { _eventsEnumerator.MoveNext(); }
            while (_eventsEnumerator.Current != null && _eventsEnumerator.Current.Time < _clock.StartTime);
        }

        private void SendEvent(MidiEvent midiEvent)
        {
            OutputDevice?.SendEvent(midiEvent);
        }

        private bool TryPlayNoteEvent(NotePlaybackEventMetadata noteMetadata, bool isNoteOnEvent, TimeSpan time, out Note note)
        {
            return TryPlayNoteEvent(noteMetadata, null, isNoteOnEvent, time, out note);
        }

        private bool TryPlayNoteEvent(PlaybackEvent playbackEvent, out Note note)
        {
            return TryPlayNoteEvent(playbackEvent.Metadata.Note, playbackEvent.Event, playbackEvent.Event is NoteOnEvent, playbackEvent.Time, out note);
        }

        private bool TryPlayNoteEvent(NotePlaybackEventMetadata noteMetadata, MidiEvent midiEvent, bool isNoteOnEvent, TimeSpan time, out Note note)
        {
            note = null;

            if (noteMetadata == null)
                return false;

            var notePlaybackData = noteMetadata.NotePlaybackData;

            var noteCallback = NoteCallback;
            if (noteCallback != null && midiEvent is NoteOnEvent)
            {
                notePlaybackData = noteCallback(noteMetadata.RawNotePlaybackData, noteMetadata.RawNote.Time, noteMetadata.RawNote.Length, time);
                noteMetadata.SetCustomNotePlaybackData(notePlaybackData);
            }

            note = noteMetadata.RawNote;

            if (noteMetadata.IsCustomNotePlaybackDataSet)
            {
                if (notePlaybackData == null || !notePlaybackData.PlayNote)
                    midiEvent = null;
                else
                {
                    note = noteMetadata.GetEffectiveNote();
                    midiEvent = midiEvent is NoteOnEvent
                        ? (MidiEvent)notePlaybackData.GetNoteOnEvent()
                        : notePlaybackData.GetNoteOffEvent();
                }
            }
            else if (midiEvent == null)
                midiEvent = isNoteOnEvent
                    ? (MidiEvent)notePlaybackData.GetNoteOnEvent()
                    : notePlaybackData.GetNoteOffEvent();

            if (midiEvent != null)
            {
                SendEvent(midiEvent);

                if (midiEvent is NoteOnEvent)
                    _activeNotesMetadata.Add(noteMetadata);
                else
                    _activeNotesMetadata.Remove(noteMetadata);
            }
            else
                note = null;

            return true;
        }

        private static ICollection<PlaybackEvent> GetPlaybackEvents(IEnumerable<ITimedObject> timedObjects, TempoMap tempoMap)
        {
            var playbackEvents = new List<PlaybackEvent>();

            foreach (var timedObject in timedObjects)
            {
                var chord = timedObject as Chord;
                if (chord != null)
                {
                    playbackEvents.AddRange(GetPlaybackEvents(chord, tempoMap));
                    continue;
                }

                var note = timedObject as Note;
                if (note != null)
                {
                    playbackEvents.AddRange(GetPlaybackEvents(note, tempoMap));
                    continue;
                }

                var timedEvent = timedObject as TimedEvent;
                if (timedEvent != null)
                    playbackEvents.Add(new PlaybackEvent(timedEvent.Event, timedEvent.TimeAs<MetricTimeSpan>(tempoMap), timedEvent.Time));
            }

            return playbackEvents.OrderBy(e => e, new PlaybackEventsComparer()).ToList();
        }

        private static IEnumerable<PlaybackEvent> GetPlaybackEvents(Chord chord, TempoMap tempoMap)
        {
            foreach (var note in chord.Notes)
            {
                foreach (var playbackEvent in GetPlaybackEvents(note, tempoMap))
                {
                    yield return playbackEvent;
                }
            }
        }

        private static IEnumerable<PlaybackEvent> GetPlaybackEvents(Note note, TempoMap tempoMap)
        {
            TimeSpan noteStartTime = note.TimeAs<MetricTimeSpan>(tempoMap);
            TimeSpan noteEndTime = TimeConverter.ConvertTo<MetricTimeSpan>(note.Time + note.Length, tempoMap);
            var noteMetadata = new NotePlaybackEventMetadata(note, noteStartTime, noteEndTime);

            yield return GetPlaybackEventWithNoteMetadata(note.TimedNoteOnEvent, tempoMap, noteMetadata);
            yield return GetPlaybackEventWithNoteMetadata(note.TimedNoteOffEvent, tempoMap, noteMetadata);
        }

        private static PlaybackEvent GetPlaybackEventWithNoteMetadata(TimedEvent timedEvent, TempoMap tempoMap, NotePlaybackEventMetadata noteMetadata)
        {
            var playbackEvent = new PlaybackEvent(timedEvent.Event, timedEvent.TimeAs<MetricTimeSpan>(tempoMap), timedEvent.Time);
            playbackEvent.Metadata.Note = noteMetadata;
            return playbackEvent;
        }

        private static IEnumerable<ITimedObject> GetTimedObjects(IEnumerable<IEnumerable<MidiEvent>> events)
        {
            ThrowIfArgument.IsNull(nameof(events), events);

            return events.Where(e => e != null)
                         .SelectMany(e => e.Where(midiEvent => midiEvent != null && !(midiEvent is MetaEvent))
                                           .GetTimedEvents()
                                           .GetTimedEventsAndNotes());
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// Releases all resources used by the current <see cref="Playback"/>.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                Stop();

                _clock.Tick -= OnClockTick;
                _clock.Dispose();
                _eventsEnumerator.Dispose();
            }

            _disposed = true;
        }

        #endregion
    }
}
