namespace Lux.Engine.Pipeline.Color;

/// <summary>
/// The Ceres 1.12.0 <c>Solver::Options</c> Lumen's <c>OptimizeHSVLut</c> writes at `0x18014feb6–0x18014ff0e` (options at `rbp+0x3b0`):
/// `+0x00 minimizer_type = 0` = LINE_SEARCH, `+0x04 line_search_direction_type = 3` = BFGS, `+0x08 line_search_type = 1` = WOLFE,
/// `+0x68 max_num_iterations = 2000`, `+0x78 num_threads = 1`, `+0xb8 function_tolerance = 1e-10`, `+0xc0 gradient_tolerance = 1e-14`
/// (`DAT_1806a07e0`), `+0xd0 linear_solver_type = DENSE_QR` (unused by the line-search minimiser), logging SILENT. Everything else keeps
/// the Ceres 1.12.0 defaults (`include/ceres/solver.h`). Lumen's Ceres is the separate `ceres.dll` (build path `3rdparty\ceres-solver-1.12.0`).
/// </summary>
public sealed class CeresLineSearchOptions
{
    public int MaxNumIterations { get; init; } = 2000;
    public double FunctionTolerance { get; init; } = 1e-10;
    public double GradientTolerance { get; init; } = 1e-14;
    public double ParameterTolerance { get; init; } = 1e-8;
    public double MinLineSearchStepSize { get; init; } = 1e-9;
    public double LineSearchSufficientFunctionDecrease { get; init; } = 1e-4;
    public double MaxLineSearchStepContraction { get; init; } = 1e-3;
    public double MinLineSearchStepContraction { get; init; } = 0.6;
    public int MaxNumLineSearchStepSizeIterations { get; init; } = 20;
    public int MaxNumLineSearchDirectionRestarts { get; init; } = 5;
    public double LineSearchSufficientCurvatureDecrease { get; init; } = 0.9;
    public double MaxLineSearchStepExpansion { get; init; } = 10.0;
    public bool UseApproximateEigenvalueBfgsScaling { get; init; } = false;
}

/// <summary>Ceres <c>TerminationType</c> (types.h): CONVERGENCE = 0, NO_CONVERGENCE = 1, FAILURE = 2.</summary>
public enum CeresTermination { Convergence = 0, NoConvergence = 1, Failure = 2 }

public sealed record CeresSummary(double[] X, CeresTermination Termination, string Message, double InitialCost, double FinalCost,
    int NumSuccessfulSteps, int NumLineSearchSteps, int NumIterations, int NumEvaluations);

/// <summary>
/// Op-for-op port of Ceres 1.12.0's <c>LineSearchMinimizer</c> (internal/ceres/line_search_minimizer.cc) with the <c>BFGS</c> direction
/// (line_search_direction.cc), the <c>WolfeLineSearch</c> (line_search.cc: bracketing + zoom phases, CUBIC interpolation) and the
/// polynomial helpers (polynomial.cc: <c>FindInterpolatingPolynomial</c> = Eigen <c>FullPivLU</c> solve, <c>MinimizePolynomial</c>,
/// closed-form roots of the derivative). The Ceres sources are kept in <c>scratch/ceres-src/</c>. Vector reductions follow Eigen's SSE2
/// redux order (<see cref="EigenRedux"/>) so the doubles track ceres.dll to the last bits where it matters.
/// </summary>
public static class CeresLineSearchMinimizer
{
    /// <summary>Cost = ½‖r‖² and gradient = Jᵀr at x (the line-search evaluator always asks for the gradient — Wolfe needs it — so every call
    /// evaluates the Jacobian, exactly like ceres.dll's 35 Evaluate calls per fit). Return false when the evaluation is invalid.</summary>
    public delegate bool Evaluator(double[] x, out double cost, double[] gradient);

    private sealed class State
    {
        public double Cost;
        public double[] Gradient;
        public double GradientSquaredNorm, GradientMaxNorm;
        public double[] SearchDirection;
        public double DirectionalDerivative;
        public double StepSize;
        public State(int n) { Gradient = new double[n]; SearchDirection = new double[n]; }
        public void CopyFrom(State o)
        {
            Cost = o.Cost; Array.Copy(o.Gradient, Gradient, Gradient.Length); GradientSquaredNorm = o.GradientSquaredNorm; GradientMaxNorm = o.GradientMaxNorm;
            Array.Copy(o.SearchDirection, SearchDirection, SearchDirection.Length); DirectionalDerivative = o.DirectionalDerivative; StepSize = o.StepSize;
        }
    }

    private sealed class Counter { public int Evaluations; }

    /// <summary>line_search_minimizer.cc: `Evaluate(evaluator, x, state)` — cost + gradient, then `projected_gradient_step = Plus(x, −g)` and the
    /// norms of `x − projected_gradient_step` (not of g itself — the subtraction rounds).</summary>
    private static bool Evaluate(Evaluator f, double[] x, State s, Counter c)
    {
        c.Evaluations++;
        if (!f(x, out s.Cost, s.Gradient)) return false;
        int n = x.Length;
        var diff = new double[n];
        for (int i = 0; i < n; i++) { double neg = -s.Gradient[i]; double proj = x[i] + neg; diff[i] = x[i] - proj; }
        s.GradientSquaredNorm = EigenRedux.SquaredNorm(diff);
        s.GradientMaxNorm = EigenRedux.MaxAbs(diff);
        return true;
    }

    public static CeresSummary Minimize(Evaluator evaluator, double[] parameters, CeresLineSearchOptions options)
    {
        int n = parameters.Length;
        var x = (double[])parameters.Clone();
        var current = new State(n); var previous = new State(n);
        var delta = new double[n]; var xPlusDelta = new double[n];
        var counter = new Counter();
        int numSuccessful = 0, numLineSearchSteps = 0, iteration = 0;

        if (!Evaluate(evaluator, x, current, counter))
            return new(x, CeresTermination.Failure, "Initial cost and jacobian evaluation failed.", double.NaN, double.NaN, 0, 0, 0, counter.Evaluations);
        double initialCost = current.Cost;
        if (current.GradientMaxNorm <= options.GradientTolerance)
            return new(x, CeresTermination.Convergence, "Gradient tolerance reached.", initialCost, current.Cost, 0, 0, 0, counter.Evaluations);

        var direction = new Bfgs(n, options.UseApproximateEigenvalueBfgsScaling);
        var lineSearchFunction = new LineSearchFunction(evaluator, n, counter);
        var wolfe = new WolfeLineSearch(options, lineSearchFunction);
        int numDirectionRestarts = 0;

        while (true)
        {
            if (iteration >= options.MaxNumIterations)
                return new(x, CeresTermination.NoConvergence, "Maximum number of iterations reached.", initialCost, current.Cost, numSuccessful, numLineSearchSteps, iteration, counter.Evaluations);
            iteration++;
            bool lineSearchStatus = true;
            if (iteration == 1) { for (int i = 0; i < n; i++) current.SearchDirection[i] = -current.Gradient[i]; }
            else lineSearchStatus = direction.NextDirection(previous, current, current.SearchDirection);

            if (!lineSearchStatus && numDirectionRestarts >= options.MaxNumLineSearchDirectionRestarts)
                return new(x, CeresTermination.Failure, "Line search direction failure: max_num_line_search_direction_restarts reached.", initialCost, current.Cost, numSuccessful, numLineSearchSteps, iteration, counter.Evaluations);
            else if (!lineSearchStatus)
            {
                numDirectionRestarts++;
                direction = new Bfgs(n, options.UseApproximateEigenvalueBfgsScaling);
                for (int i = 0; i < n; i++) current.SearchDirection[i] = -current.Gradient[i];
            }

            lineSearchFunction.Init(x, current.SearchDirection);
            current.DirectionalDerivative = EigenRedux.Dot(current.Gradient, current.SearchDirection);
            double initialStepSize = (iteration == 1 || !lineSearchStatus)
                ? Math.Min(1.0, 1.0 / current.GradientMaxNorm)
                : Math.Min(1.0, 2.0 * (current.Cost - previous.Cost) / current.DirectionalDerivative);
            if (initialStepSize < 0.0)
                return new(x, CeresTermination.Failure, "Numerical failure in line search, initial_step_size is negative.", initialCost, current.Cost, numSuccessful, numLineSearchSteps, iteration, counter.Evaluations);

            var lsSummary = wolfe.Search(initialStepSize, current.Cost, current.DirectionalDerivative);
            if (!lsSummary.Success)
                return new(x, CeresTermination.Failure, "Numerical failure in line search, failed to find a valid step size.", initialCost, current.Cost, numSuccessful, numLineSearchSteps, iteration, counter.Evaluations);

            current.StepSize = lsSummary.OptimalStepSize;
            for (int i = 0; i < n; i++) delta[i] = current.StepSize * current.SearchDirection[i];
            previous.CopyFrom(current);
            double xNorm = Math.Sqrt(EigenRedux.SquaredNorm(x));
            for (int i = 0; i < n; i++) xPlusDelta[i] = x[i] + delta[i];   // Plus (identity parameterisation)
            if (!Evaluate(evaluator, xPlusDelta, current, counter))
                return new(x, CeresTermination.Failure, "Step failed to evaluate.", initialCost, previous.Cost, numSuccessful, numLineSearchSteps, iteration, counter.Evaluations);
            var stepDiff = new double[n];
            for (int i = 0; i < n; i++) stepDiff[i] = xPlusDelta[i] - x[i];
            double stepNorm = Math.Sqrt(EigenRedux.SquaredNorm(stepDiff));
            Array.Copy(xPlusDelta, x, n);
            double costChange = previous.Cost - current.Cost;
            numLineSearchSteps += lsSummary.NumIterations;
            numSuccessful++;

            double stepSizeTolerance = options.ParameterTolerance * (xNorm + options.ParameterTolerance);
            if (stepNorm <= stepSizeTolerance)
                return new(x, CeresTermination.Convergence, "Parameter tolerance reached.", initialCost, current.Cost, numSuccessful, numLineSearchSteps, iteration, counter.Evaluations);
            if (current.GradientMaxNorm <= options.GradientTolerance)
                return new(x, CeresTermination.Convergence, "Gradient tolerance reached.", initialCost, current.Cost, numSuccessful, numLineSearchSteps, iteration, counter.Evaluations);
            double absoluteFunctionTolerance = options.FunctionTolerance * previous.Cost;
            if (Math.Abs(costChange) <= absoluteFunctionTolerance)
                return new(x, CeresTermination.Convergence, "Function tolerance reached.", initialCost, current.Cost, numSuccessful, numLineSearchSteps, iteration, counter.Evaluations);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // line_search_direction.cc: class BFGS (dense inverse Hessian, only the lower triangle is written and read — selfadjointView<Lower>)
    // ------------------------------------------------------------------------------------------------------------------------------
    private sealed class Bfgs
    {
        readonly int n; readonly bool useApproximateEigenvalueScaling; bool initialized; bool isPositiveDefinite = true;
        readonly double[] H;   // row-major n×n; entries (i ≥ j) are the stored lower triangle, (i < j) mirrors

        public Bfgs(int numParameters, bool useApproximateEigenvalueScaling)
        {
            n = numParameters; this.useApproximateEigenvalueScaling = useApproximateEigenvalueScaling;
            H = new double[n * n]; for (int i = 0; i < n; i++) H[i * n + i] = 1.0;
        }

        double Hs(int i, int j) => i >= j ? H[i * n + j] : H[j * n + i];

        public bool NextDirection(State previous, State current, double[] searchDirection)
        {
            if (!isPositiveDefinite) throw new InvalidOperationException("Ceres bug: NextDirection() called on BFGS after inverse Hessian approximation has become indefinite");
            var deltaX = new double[n]; var deltaGradient = new double[n];
            for (int i = 0; i < n; i++) { deltaX[i] = previous.SearchDirection[i] * previous.StepSize; deltaGradient[i] = current.Gradient[i] - previous.Gradient[i]; }
            double deltaXDotDeltaGradient = EigenRedux.Dot(deltaX, deltaGradient);
            const double kBFGSSecantConditionHessianUpdateTolerance = 1e-14;
            if (deltaXDotDeltaGradient <= kBFGSSecantConditionHessianUpdateTolerance)
            {
                // Skipping BFGS Update, delta_x_dot_delta_gradient too small (Secant condition).
            }
            else
            {
                if (!initialized && useApproximateEigenvalueScaling)
                {
                    double approximateEigenvalueScale = deltaXDotDeltaGradient / EigenRedux.Dot(deltaGradient, deltaGradient);
                    for (int i = 0; i < n * n; i++) H[i] *= approximateEigenvalueScale;
                }
                initialized = true;
                double rho = 1.0 / deltaXDotDeltaGradient;
                // A = delta_x * (delta_gradientᵀ · H)   (v = Hᵀ·dg = H·dg by symmetry)
                var v = new double[n];
                for (int j = 0; j < n; j++) { double acc = 0; for (int i = 0; i < n; i++) acc += deltaGradient[i] * Hs(i, j); v[j] = acc; }
                // scale = 1 + rho · dgᵀ H dg
                double scale = 1.0 + rho * EigenRedux.Dot(v, deltaGradient);
                // B (lower) = rankUpdate(delta_x, scale): B_ij = (scale · x_j) · x_i ;  H_lower += rho · (B − A − Aᵀ)
                for (int i = 0; i < n; i++)
                    for (int j = 0; j <= i; j++)
                    {
                        double bij = (scale * deltaX[j]) * deltaX[i];
                        double aij = deltaX[i] * v[j], aji = deltaX[j] * v[i];
                        H[i * n + j] += rho * ((bij - aij) - aji);
                    }
            }
            for (int i = 0; i < n; i++) { double acc = 0; for (int j = 0; j < n; j++) acc += Hs(i, j) * (-1.0 * current.Gradient[j]); searchDirection[i] = acc; }
            if (EigenRedux.Dot(searchDirection, current.Gradient) >= 0.0) { isPositiveDefinite = false; return false; }
            return true;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // line_search.cc
    // ------------------------------------------------------------------------------------------------------------------------------
    private struct FunctionSample
    {
        public double X, Value, Gradient; public bool ValueIsValid, GradientIsValid;
        public static FunctionSample ValueAndGradient(double x, double value, double gradient) => new() { X = x, Value = value, Gradient = gradient, ValueIsValid = true, GradientIsValid = true };
    }

    private sealed class LineSearchFunction
    {
        readonly Evaluator f; readonly int n; readonly Counter counter;
        readonly double[] position, direction, evaluationPoint, scaledDirection, gradient;
        public LineSearchFunction(Evaluator f, int n, Counter c) { this.f = f; this.n = n; counter = c; position = new double[n]; direction = new double[n]; evaluationPoint = new double[n]; scaledDirection = new double[n]; gradient = new double[n]; }
        public void Init(double[] pos, double[] dir) { Array.Copy(pos, position, n); Array.Copy(dir, direction, n); }
        public bool Evaluate(double x, out double value, out double g)
        {
            for (int i = 0; i < n; i++) { scaledDirection[i] = x * direction[i]; evaluationPoint[i] = position[i] + scaledDirection[i]; }
            counter.Evaluations++;
            if (!f(evaluationPoint, out value, gradient)) { g = double.NaN; return false; }
            g = EigenRedux.Dot(direction, gradient);
            return double.IsFinite(value) && double.IsFinite(g);
        }
        public double DirectionInfinityNorm() => EigenRedux.MaxAbs(direction);
    }

    private struct LsSummary { public bool Success; public double OptimalStepSize; public int NumFunctionEvaluations, NumGradientEvaluations, NumIterations; }

    private sealed class WolfeLineSearch
    {
        readonly CeresLineSearchOptions o; readonly LineSearchFunction function;
        public WolfeLineSearch(CeresLineSearchOptions o, LineSearchFunction f) { this.o = o; function = f; }

        /// <summary>LineSearch::InterpolatingPolynomialMinimizingStepSize with interpolation_type = CUBIC.</summary>
        double InterpolatingPolynomialMinimizingStepSize(in FunctionSample lowerbound, in FunctionSample previous, in FunctionSample current, double minStepSize, double maxStepSize)
        {
            if (!current.ValueIsValid) return Math.Min(Math.Max(current.X * 0.5, minStepSize), maxStepSize);
            if (!lowerbound.ValueIsValid) throw new InvalidOperationException("Ceres bug: lower-bound sample for interpolation is invalid");
            var samples = new List<FunctionSample> { lowerbound, current };
            if (previous.ValueIsValid) samples.Add(previous);
            CeresPolynomial.MinimizeInterpolatingPolynomial(samples, minStepSize, maxStepSize, out double stepSize, out _);
            return stepSize;
        }

        public LsSummary Search(double stepSizeEstimate, double initialCost, double initialGradient)
        {
            var summary = new LsSummary();
            var initialPosition = FunctionSample.ValueAndGradient(0.0, initialCost, initialGradient);
            if (!BracketingPhase(initialPosition, stepSizeEstimate, out var bracketLow, out var bracketHigh, out bool doZoomSearch, ref summary)) return summary;
            if (!doZoomSearch) { summary.OptimalStepSize = bracketLow.X; summary.Success = true; return summary; }
            if (!ZoomPhase(initialPosition, bracketLow, bracketHigh, out var solution, ref summary) && !solution.ValueIsValid) return summary;
            solution = solution.ValueIsValid && solution.Value <= bracketLow.Value ? solution : bracketLow;
            summary.OptimalStepSize = solution.X;
            summary.Success = true;
            return summary;
        }

        bool BracketingPhase(in FunctionSample initialPosition, double stepSizeEstimate, out FunctionSample bracketLow, out FunctionSample bracketHigh, out bool doZoomSearch, ref LsSummary summary)
        {
            var previous = initialPosition;
            var current = FunctionSample.ValueAndGradient(stepSizeEstimate, 0.0, 0.0); current.ValueIsValid = false;
            double descentDirectionMaxNorm = function.DirectionInfinityNorm();
            doZoomSearch = false;
            bracketLow = initialPosition; bracketHigh = default;
            summary.NumFunctionEvaluations++; summary.NumGradientEvaluations++;
            current.ValueIsValid = function.Evaluate(current.X, out current.Value, out current.Gradient);
            current.GradientIsValid = current.ValueIsValid;
            while (true)
            {
                summary.NumIterations++;
                if (current.ValueIsValid &&
                    (current.Value > (initialPosition.Value + o.LineSearchSufficientFunctionDecrease * initialPosition.Gradient * current.X) ||
                     (previous.ValueIsValid && current.Value > previous.Value)))
                {
                    doZoomSearch = true; bracketLow = previous; bracketHigh = current; break;
                }
                if (current.ValueIsValid && Math.Abs(current.Gradient) <= -o.LineSearchSufficientCurvatureDecrease * initialPosition.Gradient)
                {
                    bracketLow = current; bracketHigh = current; break;
                }
                else if (current.ValueIsValid && current.Gradient >= 0)
                {
                    doZoomSearch = true; bracketLow = current; bracketHigh = previous; break;
                }
                else if (current.ValueIsValid && Math.Abs(current.X - previous.X) * descentDirectionMaxNorm < o.MinLineSearchStepSize)
                {
                    bracketLow = current; break;
                }
                else if (summary.NumIterations >= o.MaxNumLineSearchStepSizeIterations)
                {
                    bracketLow = current.ValueIsValid && current.Value < bracketLow.Value ? current : bracketLow;
                    break;
                }
                double maxStepSize = current.ValueIsValid ? (current.X * o.MaxLineSearchStepExpansion) : current.X;
                var unusedPrevious = default(FunctionSample);
                double stepSize = InterpolatingPolynomialMinimizingStepSize(previous, unusedPrevious, current, previous.X, maxStepSize);
                if (stepSize * descentDirectionMaxNorm < o.MinLineSearchStepSize) return false;
                previous = current.ValueIsValid ? current : previous;
                current.X = stepSize;
                summary.NumFunctionEvaluations++; summary.NumGradientEvaluations++;
                current.ValueIsValid = function.Evaluate(current.X, out current.Value, out current.Gradient);
                current.GradientIsValid = current.ValueIsValid;
            }
            if (doZoomSearch && Math.Abs(bracketHigh.X - bracketLow.X) * descentDirectionMaxNorm < o.MinLineSearchStepSize) doZoomSearch = false;
            return true;
        }

        bool ZoomPhase(in FunctionSample initialPosition, FunctionSample bracketLow, FunctionSample bracketHigh, out FunctionSample solution, ref LsSummary summary)
        {
            solution = default;
            if (!(bracketLow.ValueIsValid && bracketLow.GradientIsValid)) throw new InvalidOperationException("Ceres bug: f_low input to Wolfe Zoom invalid");
            if (!bracketHigh.ValueIsValid) throw new InvalidOperationException("Ceres bug: f_high input to Wolfe Zoom invalid");
            if (bracketLow.Gradient * (bracketHigh.X - bracketLow.X) >= 0) { solution.ValueIsValid = false; return false; }
            double descentDirectionMaxNorm = function.DirectionInfinityNorm();
            while (true)
            {
                solution = bracketLow;
                if (summary.NumIterations >= o.MaxNumLineSearchStepSizeIterations) return false;
                if (Math.Abs(bracketHigh.X - bracketLow.X) * descentDirectionMaxNorm < o.MinLineSearchStepSize) return false;
                summary.NumIterations++;
                var lowerBoundStep = bracketLow.X < bracketHigh.X ? bracketLow : bracketHigh;
                var upperBoundStep = bracketLow.X < bracketHigh.X ? bracketHigh : bracketLow;
                var unusedPrevious = default(FunctionSample);
                solution.X = InterpolatingPolynomialMinimizingStepSize(lowerBoundStep, unusedPrevious, upperBoundStep, lowerBoundStep.X, upperBoundStep.X);
                summary.NumFunctionEvaluations++; summary.NumGradientEvaluations++;
                solution.ValueIsValid = function.Evaluate(solution.X, out solution.Value, out solution.Gradient);
                solution.GradientIsValid = solution.ValueIsValid;
                if (!solution.ValueIsValid) return false;
                if ((solution.Value > (initialPosition.Value + o.LineSearchSufficientFunctionDecrease * initialPosition.Gradient * solution.X)) ||
                    (solution.Value >= bracketLow.Value))
                {
                    bracketHigh = solution; continue;
                }
                if (Math.Abs(solution.Gradient) <= -o.LineSearchSufficientCurvatureDecrease * initialPosition.Gradient) break;
                else if (solution.Gradient * (bracketHigh.X - bracketLow.X) >= 0) bracketHigh = bracketLow;
                bracketLow = solution;
            }
            return true;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // polynomial.cc
    // ------------------------------------------------------------------------------------------------------------------------------
    private static class CeresPolynomial
    {
        /// <summary>Horner from the leading coefficient (polynomial.h `EvaluatePolynomial`).</summary>
        static double Evaluate(double[] p, double x) { double v = 0.0; for (int i = 0; i < p.Length; i++) v = v * x + p[i]; return v; }

        static double[] Differentiate(double[] p)
        {
            int degree = p.Length - 1;
            if (degree == 0) return new double[] { 0.0 };
            var d = new double[degree];
            for (int i = 0; i < degree; i++) d[i] = (degree - i) * p[i];
            return d;
        }

        static double[] RemoveLeadingZeros(double[] p)
        {
            int i = 0;
            while (i < p.Length - 1 && p[i] == 0.0) i++;
            return p[i..];
        }

        /// <summary>FindPolynomialRoots for degree ≤ 2 (the CUBIC interpolant's derivative); higher degrees would need the companion-matrix
        /// eigen-solver, which the Wolfe search never reaches (at most 4 constraints → cubic).</summary>
        static bool FindRoots(double[] polynomialIn, out double[] real)
        {
            var p = RemoveLeadingZeros(polynomialIn);
            int degree = p.Length - 1;
            if (degree == 0) { real = Array.Empty<double>(); return true; }
            if (degree == 1) { real = new[] { -p[1] / p[0] }; return true; }
            if (degree == 2)
            {
                double a = p[0], b = p[1], c = p[2];
                double D = b * b - 4 * a * c;
                double sqrtD = Math.Sqrt(Math.Abs(D));
                real = new double[2];
                if (D >= 0)
                {
                    if (b >= 0) { real[0] = (-b - sqrtD) / (2.0 * a); real[1] = (2.0 * c) / (-b - sqrtD); }
                    else { real[0] = (2.0 * c) / (-b + sqrtD); real[1] = (-b + sqrtD) / (2.0 * a); }
                    return true;
                }
                real[0] = -b / (2.0 * a); real[1] = -b / (2.0 * a);
                return true;
            }
            throw new NotSupportedException("polynomial degree > 2 root finding (companion matrix) is not needed by the Wolfe line search");
        }

        static void MinimizePolynomial(double[] polynomial, double xMin, double xMax, out double optimalX, out double optimalValue)
        {
            optimalX = (xMin + xMax) / 2.0;
            optimalValue = Evaluate(polynomial, optimalX);
            double xMinValue = Evaluate(polynomial, xMin);
            if (xMinValue < optimalValue) { optimalValue = xMinValue; optimalX = xMin; }
            double xMaxValue = Evaluate(polynomial, xMax);
            if (xMaxValue < optimalValue) { optimalValue = xMaxValue; optimalX = xMax; }
            if (polynomial.Length <= 2) return;
            var derivative = Differentiate(polynomial);
            if (!FindRoots(derivative, out var roots)) return;
            foreach (double root in roots)
            {
                if (root < xMin || root > xMax) continue;
                double value = Evaluate(polynomial, root);
                if (value < optimalValue) { optimalValue = value; optimalX = root; }
            }
        }

        /// <summary>FindInterpolatingPolynomial: Vandermonde-style constraints (`pow(x, degree − j)`), solved with Eigen's FullPivLU.</summary>
        static double[] FindInterpolatingPolynomial(List<FunctionSample> samples)
        {
            int numConstraints = 0;
            foreach (var s in samples) { if (s.ValueIsValid) numConstraints++; if (s.GradientIsValid) numConstraints++; }
            int degree = numConstraints - 1, m = numConstraints;
            var lhs = new double[m * m]; var rhs = new double[m];
            int row = 0;
            foreach (var s in samples)
            {
                if (s.ValueIsValid)
                {
                    for (int j = 0; j <= degree; j++) lhs[row * m + j] = Math.Pow(s.X, degree - j);
                    rhs[row] = s.Value; row++;
                }
                if (s.GradientIsValid)
                {
                    for (int j = 0; j < degree; j++) lhs[row * m + j] = (degree - j) * Math.Pow(s.X, degree - j - 1);
                    rhs[row] = s.Gradient; row++;
                }
            }
            return EigenFullPivLu.Solve(lhs, rhs, m);
        }

        public static void MinimizeInterpolatingPolynomial(List<FunctionSample> samples, double xMin, double xMax, out double optimalX, out double optimalValue)
        {
            var polynomial = FindInterpolatingPolynomial(samples);
            MinimizePolynomial(polynomial, xMin, xMax, out optimalX, out optimalValue);
            foreach (var s in samples)
            {
                if (s.X < xMin || s.X > xMax) continue;
                double value = Evaluate(polynomial, s.X);
                if (value < optimalValue) { optimalX = s.X; optimalValue = value; }
            }
        }
    }
}

/// <summary>Eigen <c>FullPivLU</c> (full pivoting, sequential row/column transpositions) and its solve. The 3.3 rank threshold
/// `|pivot| > |maxpivot| · ε · n` is implemented but disabled by default (<see cref="UseNonzeroPivots"/>): ceres.dll behaves like Eigen 3.2.</summary>
public static class EigenFullPivLu
{
    /// <summary>ceres.dll's Eigen is 3.2: <c>FullPivLU::solve</c> uses <c>nonzeroPivots()</c> (only exactly-zero pivots end the factorisation), not the
    /// 3.3 <c>rank()</c> threshold. Verified on all three L16_00466 fits: with the 3.3 rule the zoom step of iteration 1 (abscissae ~1e-6, x³ ~1e-18) is
    /// treated as rank-deficient and the trajectory leaves Lumen's at eval 3; with the 3.2 rule every Evaluate call matches to ≤4e-11 relative.</summary>
    public static bool UseNonzeroPivots = Environment.GetEnvironmentVariable("LUX_LU_RANK") != "rank";
    public static double[] Solve(double[] A, double[] b, int n)
    {
        var lu = (double[])A.Clone();
        var rowT = new int[n]; var colT = new int[n];
        double maxPivot = 0.0; int nonzeroPivots = n;
        for (int k = 0; k < n; k++)
        {
            // biggest |entry| of the bottom-right corner, column-major scan, first occurrence wins (Eigen maxCoeff visitor: strict '>')
            double biggest = Math.Abs(lu[k * n + k]); int rb = k, cb = k;
            for (int j = k; j < n; j++)
                for (int i = k; i < n; i++)
                {
                    double v = Math.Abs(lu[i * n + j]);
                    if (v > biggest) { biggest = v; rb = i; cb = j; }
                }
            rowT[k] = rb; colT[k] = cb;
            if (biggest == 0.0)
            {
                nonzeroPivots = k;
                for (int i = k; i < n; i++) { rowT[i] = i; colT[i] = i; }
                break;
            }
            if (biggest > maxPivot) maxPivot = biggest;
            if (k != rb) for (int j = 0; j < n; j++) (lu[k * n + j], lu[rb * n + j]) = (lu[rb * n + j], lu[k * n + j]);
            if (k != cb) for (int i = 0; i < n; i++) (lu[i * n + k], lu[i * n + cb]) = (lu[i * n + cb], lu[i * n + k]);
            if (k < n - 1)
            {
                // Eigen 3.2 `col /= pivot` = `col *= (1/pivot)` (DenseBase::operator/= multiplies by the reciprocal for floating scalars)
                double rcp = 1.0 / lu[k * n + k];
                for (int i = k + 1; i < n; i++) lu[i * n + k] *= rcp;
                for (int i = k + 1; i < n; i++)
                    for (int j = k + 1; j < n; j++)
                        lu[i * n + j] -= lu[i * n + k] * lu[k * n + j];
            }
        }
        // rank(): pivots above |maxpivot| · (ε · diagonalSize)
        double premultipliedThreshold = Math.Abs(maxPivot) * (2.220446049250313e-16 * n);
        int rank = 0;
        for (int i = 0; i < nonzeroPivots; i++) if (Math.Abs(lu[i * n + i]) > premultipliedThreshold) rank++;
        if (UseNonzeroPivots) rank = nonzeroPivots;   // Eigen 3.2 _solve_impl used nonzeroPivots() instead of rank()
        var x = new double[n];
        if (rank == 0) return x;
        // c = P · rhs (the row transpositions in factorisation order)
        var c = (double[])b.Clone();
        for (int k = 0; k < n; k++) if (rowT[k] != k) (c[k], c[rowT[k]]) = (c[rowT[k]], c[k]);
        // unit-lower forward substitution (column-oriented, Eigen triangular_solve_vector ColMajor)
        for (int j = 0; j < n; j++) for (int i = j + 1; i < n; i++) c[i] -= lu[i * n + j] * c[j];
        // upper back substitution over the rank×rank corner (column-oriented: `rhs[i] /= lhs(i,i)` is a true division in triangular_solve_vector)
        for (int i = rank - 1; i >= 0; i--)
        {
            c[i] /= lu[i * n + i];
            for (int j = 0; j < i; j++) c[j] -= lu[j * n + i] * c[i];
        }
        for (int i = 0; i < rank; i++) x[i] = c[i];
        // dst = Q · c (undo the column transpositions)
        for (int k = n - 1; k >= 0; k--) if (colT[k] != k) (x[k], x[colT[k]]) = (x[colT[k]], x[k]);
        return x;
    }
}

/// <summary>Eigen 3.3 SSE2 <c>redux</c> order for double vectors (packet size 2, two packet accumulators unrolled by 4, remainder scalar) —
/// what `squaredNorm()`, `dot()` and `sum()` compute in ceres.dll on aligned <c>Vector</c>s.</summary>
public static class EigenRedux
{
    public static double Sum(int n, Func<int, double> term)
    {
        const int packetSize = 2;
        int alignedSize2 = (n / (2 * packetSize)) * (2 * packetSize);
        int alignedSize = (n / packetSize) * packetSize;
        if (alignedSize == 0)
        {
            double r = term(0);
            for (int i = 1; i < n; i++) r += term(i);
            return r;
        }
        double p00 = term(0), p01 = term(1);
        if (alignedSize > packetSize)
        {
            double p10 = term(2), p11 = term(3);
            for (int i = 2 * packetSize; i < alignedSize2; i += 2 * packetSize)
            {
                p00 += term(i); p01 += term(i + 1);
                p10 += term(i + 2); p11 += term(i + 3);
            }
            p00 += p10; p01 += p11;
            if (alignedSize > alignedSize2) { p00 += term(alignedSize2); p01 += term(alignedSize2 + 1); }
        }
        double res = p00 + p01;
        for (int i = alignedSize; i < n; i++) res += term(i);
        return res;
    }
    public static double SquaredNorm(double[] v) => Sum(v.Length, i => v[i] * v[i]);
    public static double Dot(double[] a, double[] b) => Sum(a.Length, i => a[i] * b[i]);
    public static double MaxAbs(double[] v) { double m = 0; foreach (var x in v) { double a = Math.Abs(x); if (a > m) m = a; } return m; }
}

/// <summary>Eigen 3.2 <c>LDLT&lt;MatrixXd, Lower&gt;</c> (`ldlt_inplace&lt;Lower&gt;::unblocked` with the largest-diagonal pivoting and the `ε·n·max`
/// cutoff, `A21 /= akk` as a reciprocal multiply) and its solve (`P·b`, unit-lower solve, `row *= 1/D_i` unless `|D_i| ≤ 1/highest`
/// (`DAT_1806a08f8`), unit-upper solve, `Pᵀ`). This is what `OptimizeHSVLut` uses for the weighted least-squares start
/// (`FUN_180156350/1801567b0` = the decomposition of `Aᵀ·W·A`, `FUN_18015d030/18015d130` = the solve with the 3-column `Aᵀ·W·XYZ′` rhs).</summary>
public sealed class EigenLdlt
{
    readonly int n; readonly double[] m; readonly int[] trans;
    public EigenLdlt(double[] a, int size)
    {
        n = size; m = (double[])a.Clone(); trans = new int[n];
        var temp = new double[n];
        double cutoff = 0.0;
        for (int k = 0; k < n; k++)
        {
            int idx = k; double biggest = Math.Abs(m[k * n + k]);
            for (int i = k + 1; i < n; i++) { double v = Math.Abs(m[i * n + i]); if (v > biggest) { biggest = v; idx = i; } }
            if (k == 0) cutoff = Math.Abs(2.220446049250313e-16 * n * m[idx * n + idx]);
            trans[k] = idx;
            if (k != idx)
            {
                int s = n - idx - 1;
                for (int j = 0; j < k; j++) (m[k * n + j], m[idx * n + j]) = (m[idx * n + j], m[k * n + j]);           // row(k).head(k) ↔ row(idx).head(k)
                for (int i = 0; i < s; i++) (m[(idx + 1 + i) * n + k], m[(idx + 1 + i) * n + idx]) = (m[(idx + 1 + i) * n + idx], m[(idx + 1 + i) * n + k]);   // col(k).tail(s) ↔ col(idx).tail(s)
                (m[k * n + k], m[idx * n + idx]) = (m[idx * n + idx], m[k * n + k]);
                for (int i = k + 1; i < idx; i++) (m[i * n + k], m[idx * n + i]) = (m[idx * n + i], m[i * n + k]);
            }
            int rs = n - k - 1;
            if (k > 0)
            {
                for (int j = 0; j < k; j++) temp[j] = m[j * n + j] * m[k * n + j];                 // temp = D.head(k) ⊙ A10ᵀ
                m[k * n + k] -= EigenRedux.Sum(k, j => m[k * n + j] * temp[j]);                    // akk −= A10·temp
                for (int i = k + 1; i < n; i++) { double acc = 0.0; for (int j = 0; j < k; j++) acc += m[i * n + j] * temp[j]; m[i * n + k] -= acc; }   // A21 −= A20·temp
            }
            if (rs > 0 && Math.Abs(m[k * n + k]) > cutoff)
            {
                double rcp = 1.0 / m[k * n + k];
                for (int i = k + 1; i < n; i++) m[i * n + k] *= rcp;
            }
        }
    }

    /// <summary>Solve for one right-hand side.</summary>
    public double[] Solve(double[] b)
    {
        var x = (double[])b.Clone();
        for (int k = 0; k < n; k++) if (trans[k] != k) (x[k], x[trans[k]]) = (x[trans[k]], x[k]);
        for (int j = 0; j < n; j++) for (int i = j + 1; i < n; i++) x[i] -= m[i * n + j] * x[j];               // L⁻¹ (unit lower, column-oriented)
        const double tolerance = 5.562684646268003e-309;                                                        // 1 / numeric_limits<double>::max()
        for (int i = 0; i < n; i++) { double d = m[i * n + i]; if (Math.Abs(d) > tolerance) x[i] *= 1.0 / d; else x[i] = 0.0; }
        for (int j = n - 1; j >= 0; j--) for (int i = 0; i < j; i++) x[i] -= m[j * n + i] * x[j];              // L⁻ᵀ (unit upper, column-oriented)
        for (int k = n - 1; k >= 0; k--) if (trans[k] != k) (x[k], x[trans[k]]) = (x[trans[k]], x[k]);
        return x;
    }
}
