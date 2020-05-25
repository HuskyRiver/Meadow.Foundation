﻿using System.Threading;
using System.Threading.Tasks;
using Meadow.Hardware;
using Meadow.Peripherals.Sensors.Rotary;

namespace Meadow.Foundation.Sensors.Rotary
{
    /// <summary>
    /// Digital rotary encoder that uses two-bit Gray Code to encode rotation.
    /// </summary>
    public class RotaryEncoder : IRotaryEncoder
    {
        #region Properties

        /// <summary>
        /// Returns the pin connected to the A-phase output on the rotary encoder.
        /// </summary>
        public IDigitalInputPort APhasePort { get; }

        /// <summary>
        /// Returns the pin connected to the B-phase output on the rotary encoder.
        /// </summary>
        public IDigitalInputPort BPhasePort { get; }

        /// <summary>
        /// Raised when the rotary encoder is rotated and returns a RotaryTurnedEventArgs object which describes the direction of rotation.
        /// </summary>
        public event RotaryTurnedEventHandler Rotated = delegate { };

        #endregion

        #region Member variables / fields

        /// <summary>
        /// Whether or not we're processing the gray code (encoding of rotational information)
        /// </summary>
        protected bool _processing = false;

        /// <summary>
        /// Lock object to ensure events aren't overriding eachothers information
        /// </summary>
        private readonly object _lock = new object();

        /// <summary>
        /// Signals that the aPhase has triggered an event
        /// </summary>
        protected bool _aTriggered = false;


        /// <summary>
        /// Two sets of gray code results to determine direction of rotation. Note that this is no longer used in the seperate event logic
        /// </summary>
        protected TwoBitGrayCode[] _results = new TwoBitGrayCode[2]; //no longer needed

        #endregion

        #region Constructors

        /// <summary>
        /// Instantiate a new RotaryEncoder on the specified pins.
        /// </summary>
        /// <param name="device"></param>
        /// <param name="aPhasePin"></param>
        /// <param name="bPhasePin"></param>
        public RotaryEncoder(IIODevice device, IPin aPhasePin, IPin bPhasePin) :
            this(device.CreateDigitalInputPort(aPhasePin, InterruptMode.EdgeBoth, ResistorMode.PullUp, 10, 50), //Signifcantly reduced debounce as this was causing reading issues if RotaryEncoder was turned quickly.
                 device.CreateDigitalInputPort(bPhasePin, InterruptMode.EdgeBoth, ResistorMode.PullUp, 10, 50))
        { }

        /// <summary>
        /// Instantiate a new RotaryEncoder on the specified ports
        /// </summary>
        /// <param name="aPhasePort"></param>
        /// <param name="bPhasePort"></param>
        public RotaryEncoder(IDigitalInputPort aPhasePort, IDigitalInputPort bPhasePort)
        {
            APhasePort = aPhasePort;
            BPhasePort = bPhasePort;

            APhasePort.Changed += PhaseAPinChanged;
            BPhasePort.Changed += PhaseBPinChanged;
        }

        #endregion

        #region Methods

         private void resetFlags()
        {
            _processing = false;
            _aTriggered = false;
        }

        /// <summary>
        /// This event monitors the bPhase pin. It will raise an event on rotation if it is the second event fired (_processing = true) and bPhase triggered first (CounterClockwise rotation)
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void PhaseAPinChanged(object sender, DigitalInputPortEventArgs e)
        {
            // Lock the thread so protected variables are not modified while process
            lock (_lock)
            {
                if (_processing)
                {
                    if (_aTriggered)
                    {
                        //This is an invalid event because a-triggers can't toggle the processing flag
                        // However, this can be the start of a new triggering event! 
                        // Don't change _processing or _aTriggered because if we are down this path it either means it is invalid or that b will soon trigger
                    }
                    else //Means b triggered first
                    {
                        OnRaiseRotationEvent(RotationDirection.CounterClockwise);
                        resetFlags(); // After successful flag!
                    }
                }
                else
                {
                    // toggle our processing flag
                    _processing = !_processing;
                }
                _aTriggered = true;
            }


            // to address issues where the flags have been reversed for whatever reason after time reset the flags
            new Task(() =>
            {
                Thread.Sleep(50); //all events should be within 50 ms of each other. This may cause issue if people are turning at super human speed
                resetFlags();
            }).Start();
        }

        /// <summary>
        /// This event monitors the bPhase pin. It will raise an event on rotation if it is the second event fired (_processing = true) and aPhase triggered first (clockwise rotation)
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void PhaseBPinChanged(object sender, DigitalInputPortEventArgs e)
        {
            lock (_lock)
            {
                if (_processing)
                {
                    if (_aTriggered) //a triggered first
                    {
                        OnRaiseRotationEvent(RotationDirection.Clockwise);
                        resetFlags(); // after successful flag!
                    }
                    else
                    {
                        //This is an invalid path because if we are processing it means it is the second event fired. reset flags and return
                        // leave processing flag alone because if a now triggers it is a valid sequence
                    }
                    //ProcessRotationResults();
                }
                else
                {
                    // toggle our processing flag - only toggle selectively to catch misfiring events
                    _processing = !_processing;
                }
                _aTriggered = false;
            }

        }

        private void PhasePinChanged(object sender, DigitalInputPortEventArgs e)
        {
            //Console.WriteLine((!_processing ? "1st result: " : "2nd result: ") + "A{" + (APhasePin.Read() ? "1" : "0") + "}, " + "B{" + (BPhasePin.Read() ? "1" : "0") + "}");

            // the first time through (not processing) store the result in array slot 0.
            // second time through (is processing) store the result in array slot 1.
            _results[_processing ? 1 : 0].APhase = APhasePort.State;
            _results[_processing ? 1 : 0].BPhase = BPhasePort.State;

            // if this is the second result that we're reading, we should now have 
            // enough information to know which way it's turning, so process the
            // gray code
            if (_processing)
            {
                ProcessRotationResults();
            }

            // toggle our processing flag
            _processing = !_processing;
        }

        /// <summary>
        /// Determines the direction of rotation when the PhasePinChanged event is triggered
        /// </summary>
        protected void ProcessRotationResults()
        {
            // if there hasn't been any change, then it's a garbage reading. so toss it out.
            if (_results[0].APhase == _results[1].APhase &&
                _results[0].BPhase == _results[1].BPhase)
                //Console.WriteLine("Garbage");
                return;

            // start by reading the a phase pin. if it's High
            if (_results[0].APhase)
            {
                // if b phase went down, then it spun counter-clockwise
                OnRaiseRotationEvent(_results[1].BPhase ? RotationDirection.CounterClockwise : RotationDirection.Clockwise);
            }
            // if a phase is low
            else
            {
                // if b phase went up, then it spun counter-clockwise
                OnRaiseRotationEvent(_results[1].BPhase ? RotationDirection.CounterClockwise : RotationDirection.Clockwise);
            }
        }

        /// <summary>
        /// Invokes the RotaryTurnedEventHandler, passing the direction in the RotaryTurnedEventArgs
        /// </summary>
        /// <param name="direction"></param>
        protected void OnRaiseRotationEvent(RotationDirection direction)
        {
            Rotated?.Invoke(this, new RotaryTurnedEventArgs(direction));
        }

        #endregion
    }
}