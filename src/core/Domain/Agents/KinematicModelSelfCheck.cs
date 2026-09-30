using AICopilotCore.Domain;

namespace AICopilotCore.Domain.Agents;

// Auto-comprobacion sin simulador del integrador cinematico. No forma parte
// del flujo normal; se invoca a mano (p. ej. desde un proyecto de scratch):
//   var (ok, report) = KinematicModelSelfCheck.Run();
public static class KinematicModelSelfCheck
{
    public static (bool Ok, string Report) Run()
    {
        var log = new System.Text.StringBuilder();
        bool ok = true;
        void Check(string name, bool pass, string detail)
        {
            ok &= pass;
            log.AppendLine($"{(pass ? "OK  " : "FAIL")} {name}: {detail}");
        }

        // 1) Vuelo nivelado: 5000 ft, ~300 kt IAS, palanca 0 durante 60 s.
        {
            var m = AirModel(headingDeg: 90, altM: 1524, iasKt: 300);
            float a0 = m.AltFt, i0 = m.IasKt;
            for (int i = 0; i < 1200; i++) m.Step(0.05f);
            Check("nivelado", Math.Abs(m.AltFt - a0) < 50 && Math.Abs(m.IasKt - i0) < 5,
                  $"dAlt={m.AltFt - a0:0.0} ft, dIAS={m.IasKt - i0:0.0} kt, hdg={m.HeadingDeg:0.0}");
        }

        // 2) Viraje a 30 deg de alabeo sostenido con la G que pide.
        {
            var m = AirModel(headingDeg: 0, altM: 3000, iasKt: 320);
            float a0 = m.AltFt;
            float hdg0 = m.HeadingDeg, hdgPrev = hdg0;
            double unwrapped = 0;
            const double Target = 30.0;
            double t = 0, rateSum = 0; int rateN = 0;
            for (int i = 0; i < 600; i++, t += 0.05)
            {
                m.RollStick = Math.Clamp((float)((Target - m.BankDeg) * 0.08), -1f, 1f);
                float need = F14Aero.LoadFactorForBank(m.BankDeg) - 1f;
                m.PitchStick = need / m.Profile.GPerStickUnit(m.IasKt);
                m.Step(0.05f);
                double d = m.HeadingDeg - hdgPrev;
                if (d > 180) d -= 360; else if (d < -180) d += 360;
                hdgPrev = m.HeadingDeg;
                unwrapped += d;
                if (t > 10) { rateSum += d / 0.05; rateN++; }
            }
            double rate = rateSum / rateN;
            float expected = F14Aero.TurnRateDegPerSec(m.TasKt, 30f);
            Check("viraje a la derecha", unwrapped > 0 && Math.Abs(m.BankDeg - 30) < 3 &&
                  Math.Abs(rate - expected) < 0.2 * expected && Math.Abs(m.AltFt - a0) < 300,
                  $"bank={m.BankDeg:0.0}, rumbo {unwrapped:0}deg en 30 s, rate={rate:0.00} vs {expected:0.00} deg/s, dAlt={m.AltFt - a0:0} ft");
        }

        // 3) Recorte de G: palanca a fondo a 500 kt se adapta, no aborta.
        {
            var m = AirModel(headingDeg: 0, altM: 3000, iasKt: 500);
            m.PitchStick = 1f;
            for (int i = 0; i < 40; i++) m.Step(0.05f);
            float usable = m.Profile.UsableLoadFactor(m.IasKt, m.WeightLb, m.Mach);
            Check("recorte de G", m.GNormal <= usable + 0.05f && m.AdaptationText.Length > 0,
                  $"G={m.GNormal:0.00} <= {usable:0.00}; texto=\"{m.AdaptationText}\"");
        }

        // 4) Despegue: gases a tope, rotar a Vr, debe despegar.
        {
            var m = new KinematicFlightModel(F14Profile.Instance);
            m.Capture(0, 0, 0, 0, 0, 0, 90, 0, 0, elevM: 10, gearRatio: 1, groundY: double.NaN, assumedAglM: 1500);
            m.ParkingBrake = false;
            m.FlapTarget = 0.2f;
            m.ThrottleCmd = 1f;
            double t = 0, liftT = double.NaN;
            for (int i = 0; i < 2400 && double.IsNaN(liftT); i++, t += 0.05)
            {
                if (m.IasKt >= 145f) m.PitchStick = Math.Clamp((12f - m.PitchDeg) * 0.1f, 0f, 1f);
                m.Step(0.05f);
                if (!m.OnGround) liftT = t;
            }
            Check("despegue", !double.IsNaN(liftT),
                  $"liftoff t={liftT:0.0} s, IAS={m.IasKt:0} kt, pitch={m.PitchDeg:0.0}, x={m.X:0} m");
        }
        return (ok, log.ToString());
    }

    private static KinematicFlightModel AirModel(double headingDeg, double altM, double iasKt)
    {
        var m = new KinematicFlightModel(F14Profile.Instance);
        float altFt = (float)(altM * KinematicFlightModel.MToFt);
        double tas = F14Aero.TrueAirspeedKt((float)iasKt, altFt) / KinematicFlightModel.MpsToKt;
        double psi = headingDeg * Math.PI / 180.0;
        m.Capture(0, altM, 0, tas * Math.Sin(psi), 0, -tas * Math.Cos(psi),
                  headingDeg, 3, 0, elevM: altM, gearRatio: 0, groundY: double.NaN, assumedAglM: 1500);
        return m;
    }
}
