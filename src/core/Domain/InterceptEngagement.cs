namespace AICopilotCore.Domain;

// Director de engagement + leyes de velocidad por situacion.
// No vuela: clasifica geometria propio/blanco y acota el cierre. El planner
// elige modulo (HeadOn / Stern / Formation) segun esto.
//
// Bug dominante que mata: cerca del blanco (rng < ~5-8 km) DesiredGs no
// puede ir a Vmax; debe converger a Vblanco + cierre acotado.

// Geometria gruesa (lado / aspecto). Conservada para ChooseApproachSide.
public enum EngagementGeometry
{
    Stern,
    BeamRight,
    BeamLeft,
    HeadOn,
    Overtaking,
}

// Situacion del director: elige la ley de guiado.
public enum InterceptSituation
{
    HeadOn,      // frontal / casi frontal: rodeo + igualar velocidad
    SternFar,    // detras, lejos: cierre con tope, no sprint ciego
    SternNear,   // detras, cerca: captura (tgt + cierre racionado)
    BeamFar,     // costado / pasillo
    Overtaking,  // por delante: frenar y dejarse adelantar
}

public static class InterceptEngagement
{
    // A partir de esta distancia al avion blanco se congela el lado.
    public const double SideLockRangeM = 12000.0;
    // Si ya estamos descentrados mas que esto, se conserva el lado ocupado
    // (no se cruza el eje "para acortar").
    public const double SidePreferM = 400.0;
    // Estacion casi en el eje de cola (|Right| bajo): se vuela un pasillo
    // lateral hasta estar detras, y luego se desliza al eje.
    public const double OnAxisRightM = 80.0;
    // Ancho del pasillo respecto al eje del blanco (metros a la derecha si
    // ApproachSide = +1).
    public const double CorridorSideM = 1000.0;

    // --- Rangos del director de velocidad ------------------------------------
    // Por encima: se permite sprint (tope alto). Entre Match y Hard: se
    // estrecha el cierre. Por debajo de Hard: techo = Vtgt + cierre bajo.
    public const double SpeedMatchRangeM = 8000.0;
    public const double SpeedHardRangeM = 5000.0;
    public const double SternNearRangeM = 5500.0;

    // Clasifica la geometria propia vs el AVION blanco (no vs la estacion).
    // along >0 = por delante del blanco; cross >0 = a su derecha.
    public static EngagementGeometry Classify(double alongM, double crossM)
    {
        if (alongM > 500.0 && Math.Abs(crossM) < 1500.0) return EngagementGeometry.Overtaking;
        if (alongM < -800.0 && Math.Abs(crossM) < Math.Abs(alongM) * 0.5)
            return EngagementGeometry.Stern;
        if (alongM > -200.0 && Math.Abs(crossM) < 800.0 && alongM < 800.0)
            return EngagementGeometry.HeadOn;
        return crossM >= 0.0 ? EngagementGeometry.BeamRight : EngagementGeometry.BeamLeft;
    }

    // Clasificador del director: situacion → modulo de ley.
    // along/cross/range respecto al AVION blanco (metros ENU del planner).
    public static InterceptSituation ClassifySituation(double alongM, double crossM, double rangeM)
    {
        double absCross = Math.Abs(crossM);

        // Por delante en el eje: overtaking (o head-on si casi alineados y cerca).
        if (alongM > 400.0 && absCross < Math.Max(1200.0, 0.55 * alongM))
        {
            if (alongM < 2500.0 && absCross < 900.0 && rangeM < SpeedHardRangeM)
                return InterceptSituation.HeadOn;
            return InterceptSituation.Overtaking;
        }

        // Detras en embudo de cola.
        bool stern = alongM < -500.0 && absCross < Math.Abs(alongM) * 0.75 + 900.0;
        if (stern)
            return rangeM <= SternNearRangeM
                ? InterceptSituation.SternNear
                : InterceptSituation.SternFar;

        // Frontal / colision: delante o abeam del morro, poco lateral.
        if (alongM > -1200.0 && alongM < 2000.0 && absCross < 2200.0 &&
            absCross < Math.Max(900.0, Math.Abs(alongM) * 0.85 + 400.0))
            return InterceptSituation.HeadOn;

        return InterceptSituation.BeamFar;
    }

    // Cierre relativo maximo (m/s) permitido a esta distancia al avion blanco.
    // Nunca "sprint a Vmax" bajo SpeedHardRangeM.
    public static double MaxClosureMps(double rangeToTargetM, InterceptSituation sit)
    {
        double r = Math.Max(rangeToTargetM, 0.0);
        // Es un techo de SEGURIDAD: el frenado real lo impone el perfil de
        // deceleracion del planner (sqrt(2*a*d) con el retardo descontado).
        // Antes era 40 m/s a 5 km y 22 a 2.5 km: con el blanco a 340 kt el
        // caza iba a +30 kt y tardaba minutos en recorrer 5 km.
        double baseCap;
        if (r >= SpeedMatchRangeM)
            baseCap = 160.0;                         // lejos: ~310 kt de cierre
        else if (r >= SpeedHardRangeM)
        {
            // 8 km → 115, 5 km → 80
            double t = (r - SpeedHardRangeM) / (SpeedMatchRangeM - SpeedHardRangeM);
            baseCap = 80.0 + t * 35.0;
        }
        else if (r >= 2500.0)
        {
            // 5 km → 80, 2.5 km → 50
            double t = (r - 2500.0) / (SpeedHardRangeM - 2500.0);
            baseCap = 50.0 + t * 30.0;
        }
        else if (r >= 800.0)
        {
            // 2.5 km → 50, 0.8 km → 25
            double t = (r - 800.0) / (2500.0 - 800.0);
            baseCap = 25.0 + t * 25.0;
        }
        else
            baseCap = 10.0 + 15.0 * (r / 800.0);     // zona de estacion

        return sit switch
        {
            // Head-on: igualar ANTES de cerrar; tope muy bajo cerca.
            // Lejos (>8 km) se permite cierre moderado para no llegar tarde.
            InterceptSituation.HeadOn => Math.Min(baseCap,
                r < SpeedHardRangeM ? 12.0
                : r < SpeedMatchRangeM ? 35.0
                : 90.0),
            // Overtaking: no cerrar (cierre negativo lo pone CapDesiredGs).
            InterceptSituation.Overtaking => Math.Min(baseCap, 5.0),
            InterceptSituation.SternNear => Math.Min(baseCap, 90.0),
            InterceptSituation.SternFar => baseCap,
            InterceptSituation.BeamFar => Math.Min(baseCap, r < SpeedHardRangeM ? 60.0 : baseCap),
            _ => baseCap,
        };
    }

    // Techo de GS deseada: Vtgt + cierre acotado. Mata el overshoot a Vmax
    // cuando rng < 5-8 km y el blanco va ~200 kt.
    public static double CapDesiredGs(double rawGsMps, double tgtGsMps, double rangeToTargetM,
                                      OwnLimits limits, InterceptSituation sit)
    {
        double maxClose = MaxClosureMps(rangeToTargetM, sit);
        double ceiling;
        if (sit == InterceptSituation.Overtaking)
        {
            // Frenar por debajo del blanco para que nos adelante.
            double drop = Math.Clamp(12.0 + 0.02 * Math.Max(rangeToTargetM, 0.0), 12.0, 40.0);
            ceiling = tgtGsMps - drop + maxClose; // maxClose~5 → casi drop
            ceiling = Math.Min(ceiling, tgtGsMps - 8.0);
        }
        else
        {
            ceiling = tgtGsMps + maxClose;
        }

        // Bajo HardRange: el techo NUNCA puede ser Vmax del avion si el blanco
        // va mucho mas lento (el caso F-14 ~500 kt vs blanco ~200 kt).
        if (rangeToTargetM < SpeedHardRangeM)
            ceiling = Math.Min(ceiling, tgtGsMps + maxClose);

        ceiling = Math.Min(ceiling, limits.VmaxGsMps);
        return Math.Clamp(rawGsMps, limits.VminGsMps, Math.Max(ceiling, limits.VminGsMps));
    }

    // Ley Stern/Capture: velocidad = tgt + cierre racionado por freno y por
    // distancia (cap bajo cerca). Funcion pura.
    public static double SternCaptureGs(double tgtGsMps, double rangeToTargetM,
                                        double brakeDistM, double ownGsMps,
                                        OwnLimits limits, InterceptSituation sit)
    {
        double aDec = 0.75 * InterceptPlanner.Decel(limits, 0.5 * (ownGsMps + tgtGsMps));
        double wBrake = Math.Sqrt(2.0 * aDec * Math.Max(brakeDistM, 0.0));
        double wCap = MaxClosureMps(rangeToTargetM, sit);
        double w = Math.Min(wBrake, wCap);
        return CapDesiredGs(tgtGsMps + w, tgtGsMps, rangeToTargetM, limits, sit);
    }

    // Ley Head-on: igualar / bajar hacia Vtgt (sin pedir Vmax). El rumbo lo
    // fuerza el planner con corredor/rodeo.
    public static double HeadOnMatchGs(double tgtGsMps, double rangeToTargetM,
                                       OwnLimits limits)
    {
        // Un poco por encima solo si lejos; cerca = casi match.
        double bleed = MaxClosureMps(rangeToTargetM, InterceptSituation.HeadOn);
        return CapDesiredGs(tgtGsMps + bleed, tgtGsMps, rangeToTargetM, limits,
                            InterceptSituation.HeadOn);
    }

    // Elige el lado de engagement (+1 derecha del blanco, -1 izquierda).
    // Si alreadyLocked, devuelve lockedSide sin cambiar.
    public static int ChooseApproachSide(double crossVsTargetM, bool alreadyLocked, int lockedSide,
                                         double stationRightM)
    {
        if (alreadyLocked) return lockedSide == 0 ? 1 : Math.Sign(lockedSide);

        // Ya descentrados: conservar el pasillo ocupado.
        if (Math.Abs(crossVsTargetM) >= SidePreferM)
            return crossVsTargetM >= 0.0 ? 1 : -1;

        // Estacion con lateral propio (Paralelo I/D): preferir ese lado.
        if (Math.Abs(stationRightM) >= OnAxisRightM)
            return stationRightM >= 0.0 ? 1 : -1;

        // Empate / casi alineados: doctrinal derecha.
        return 1;
    }

    // ¿Hay que forzar pasillo lateral? Cola alta / Above / Below (Right~0)
    // mientras no estemos bien detras en el embudo de cola.
    public static bool NeedsCorridor(double stationRightM, double alongVsTargetM, double crossVsTargetM)
    {
        if (Math.Abs(stationRightM) >= OnAxisRightM) return false;
        // Ya en stern profundo y cerca del eje: se puede apuntar al RP/estacion.
        if (alongVsTargetM < -1500.0 && Math.Abs(crossVsTargetM) < CorridorSideM * 0.55)
            return false;
        return true;
    }

    // Head-on / beam: siempre pasillo hasta estar detras (aunque la estacion
    // tenga lateral).
    public static bool ForceCorridor(InterceptSituation sit, double alongVsTargetM)
    {
        if (sit is InterceptSituation.HeadOn or InterceptSituation.BeamFar)
            return alongVsTargetM > -1500.0;
        return false;
    }
}
