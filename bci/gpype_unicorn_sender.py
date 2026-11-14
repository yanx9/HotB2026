"""Reference g.Pype pipeline: Unicorn Hybrid Black -> standard UDPSender.

Run in your g.Pype environment, separately from the receiver / calibrator.
Uses the documented g.Pype 4.x API; requires the gui and devices extras.
The raw EEG branch preserves the 55-95 Hz band needed by the jaw detector.
"""

import argparse


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--host", default="127.0.0.1", help="adres odbiornika EEG")
    cli.add_argument("--port", type=int, default=56000)
    cli.add_argument("--serial", help="identyfikator Unicorna; domyślnie pierwsze znalezione urządzenie")
    args = cli.parse_args()
    import gpype as gp

    app = gp.MainApp()
    pipeline = gp.Pipeline()
    unicorn = gp.HybridBlack(serial=args.serial, channel_count=8, frame_size=4,
                            include_accel=False, include_gyro=False, include_aux=False)
    sender = gp.UDPSender(ip=args.host, port=args.port, frame_size_out=4)
    scope = gp.TimeSeriesScope(name="Unicorn EEG", amplitude_limit=100, time_window=10)
    pipeline.connect(unicorn, sender)
    pipeline.connect(unicorn, scope)
    app.add_widget(scope)
    try:
        pipeline.start()
        # Required on macOS: the Unicorn Bluetooth driver needs the main event loop.
        app.run()
    finally:
        pipeline.stop()
        pipeline.close()


if __name__ == "__main__":
    main()
