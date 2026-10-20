using UdonSharp;
using UnityEngine;

namespace LogicCuteGuy.Wasm2Hlsl.Samples
{
    /// <summary>
    /// One on-panel piano key. An EventTrigger sends Down on pointer down and Up on pointer up / exit,
    /// so the note sounds for as long as the key is held.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class JunoKey : UdonSharpBehaviour
    {
        public JunoSynth synth;
        [Tooltip("MIDI note number.")]
        public int note;

        private bool _down;
        private int _downFrame = -1;

        public void Down()
        {
            // ClientSim can deliver one click as several pointer-downs in the same frame.
            if (_down && Time.frameCount == _downFrame) return;
            _downFrame = Time.frameCount;
            if (_down) synth.NoteOff(note); // missed pointer-up: retrigger

            _down = true;
            synth.NoteOn(note, 0.8f);
        }

        public void Up()
        {
            if (!_down) return; // pointer exit without a press
            _down = false;
            synth.NoteOff(note);
        }
    }
}
