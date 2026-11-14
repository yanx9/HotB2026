"""Generic bridge entry point: g.Pype UDPSender -> calibrated signals -> Unity.

    python bci/bci_bridge.py --profile data/calibration/participant_gpype.json

The implementation and backwards-compatible LSL entry point share one runtime.
"""

from lsl_bridge import main


if __name__ == "__main__":
    main(default_source="gpype")
