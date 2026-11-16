using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Lux.App.Fake;
using Lux.App.ViewModels;
using Lux.Engine.Lri;
using Lux.Library;
using Lux.Library.Edit;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>
/// The two hints from the camera's motion sensor. "Possibly blurred" is a quiet mark on photos whose predicted motion blur is above the
/// limit, and a filter. "Level" in Develop sets the rotation that the recorded gravity direction suggests, as one step that can be undone.
/// The tests with real captures run only when LUX_TEST_CAPTURES names their folder.
/// </summary>
public sealed class MotionHintsTests
{
    readonly ITestOutputHelper _out;
    public MotionHintsTests(ITestOutputHelper output) => _out = output;

    // ------------------------------------------------------------------------------------------------ possibly blurred (fake library)

    [AvaloniaFact]
    public async Task Possibly_blurred_photos_have_a_quiet_mark_and_a_filter()
    {
        await using var h = await Harness.CreateAsync();
        await h.SettleAsync();
        await h.SelectScopeAsync("all");
        var lib = h.Library;
        var marked = lib.Items.Where(i => i.PossiblyBlurred).ToList();
        Assert.NotEmpty(marked);
        Assert.Contains(lib.Items, i => !i.PossiblyBlurred);
        // the mark follows the limit of the library, and never shows on a photo that cannot be read
        Assert.All(marked, i => Assert.True(i.Item.PredictedBlurPixels > CaptureItem.PossiblyBlurredPixels));
        Assert.All(lib.Items.Where(i => i.HasProblem), i => Assert.False(i.PossiblyBlurred));
        // the wording stays a hint
        Assert.All(marked, i => Assert.Contains("possibly blurred", i.Meta));
        Assert.StartsWith("Possibly blurred", CaptureItemViewModel.BlurTip);
        Assert.All(marked, i => Assert.StartsWith("Possibly blurred", i.BlurNote));
        Assert.All(lib.Items.Where(i => !i.PossiblyBlurred), i => Assert.Null(i.BlurNote));
        Assert.Equal("Possibly blurred", SmartFilters.Label(SmartFilter.PossiblyBlurred));
        lib.Click(marked[0], false, false);
        await h.SettleAsync();
        h.Shot("motion-01-library-blur-marks");

        // the filter beside the other quick filters
        var chip = lib.Chips.Single(c => c.Filter == SmartFilter.PossiblyBlurred);
        Assert.Equal("Possibly blurred", chip.BaseLabel);
        lib.SelectChipCommand.Execute(chip);
        await h.SettleAsync();
        Assert.NotEmpty(lib.Items);
        Assert.All(lib.Items, i => Assert.True(i.Item.PossiblyBlurred));
        int count = h.Services.Library.GetSmartFilterCounts()[SmartFilter.PossiblyBlurred];
        Assert.Equal(count, h.Services.Library.Count(new CaptureQuery().With(SmartFilter.PossiblyBlurred)));
        Assert.Equal(h.Services.Library.Count(new CaptureQuery()), count + h.Services.Library.Count(new CaptureQuery { PossiblyBlurred = false }));
        h.Shot("motion-02-library-blur-filter");
    }

    // ------------------------------------------------------------------------------------------------ level (fake Develop session)

    static async Task<Harness> OpenWhereAsync(Func<LevelSuggestion, bool> wanted)
    {
        var h = await Harness.CreateAsync();
        await h.SettleAsync();
        await h.MockupSelectionAsync();
        await h.SelectScopeAsync("all");
        foreach (var item in h.Library.Items.Where(i => !i.HasProblem && !i.IsBusy && i.Item.HeldOrientation is PhotoOrientation.Normal or PhotoOrientation.Unknown).Take(60))
        {
            h.Library.Click(item, false, false);
            h.Vm.OpenInDevelop(item);
            await h.SettlePictureAsync();
            if (h.Develop().Session is { } s && s.Geometry.IsIdentity && wanted(s.LevelSuggestion)) return h;
        }
        await h.DisposeAsync();
        throw new InvalidOperationException("no fake photo with the wanted level suggestion");
    }

    [AvaloniaFact]
    public async Task Level_sets_the_suggested_angle_as_one_step_that_can_be_undone()
    {
        await using var h = await OpenWhereAsync(l => l.Available && Math.Abs(l.Degrees) >= 1);
        var d = h.Develop();
        var s = h.Session();
        var suggestion = s.LevelSuggestion;
        Assert.True(d.CanLevel);
        Assert.StartsWith("Suggested: turn ", d.LevelNote);
        Assert.Contains(suggestion.Degrees >= 0 ? "left" : "right", d.LevelNote);
        int steps = s.History.Count;

        d.LevelCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.Equal(suggestion.Degrees, s.Geometry.LevelDegrees, 6);
        Assert.True(s.Geometry.HasCrop);                                  // no empty corners: the crop is a little smaller
        Assert.True(s.Geometry.Crop.Width > 0.8 && s.Geometry.Crop.Height > 0.8);
        Assert.Equal(steps + 1, s.History.Count);
        Assert.Equal("Level", s.History[^1]);
        Assert.Contains("leveled", d.GeometryText);
        Assert.False(d.CanLevel);                                         // the suggestion is applied: nothing more to do
        Assert.StartsWith("Leveled", d.LevelNote);
        h.DevelopView().FindControl<Button>("LevelButton")!.BringIntoView();
        await h.SettlePictureAsync();
        h.Shot("motion-03-develop-level");

        // a quarter turn keeps the level rotation
        d.RotateRightCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.Equal(suggestion.Degrees, s.Geometry.LevelDegrees, 6);
        d.UndoCommand.Execute(null);
        await h.SettlePictureAsync();

        d.UndoCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.False(s.Geometry.HasLevel);
        Assert.False(s.Geometry.HasCrop);
        Assert.True(d.CanLevel);
        d.RedoCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.Equal(suggestion.Degrees, s.Geometry.LevelDegrees, 6);

        // the geometry reset removes it
        d.ResetGeometryCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.False(s.Geometry.HasLevel);
        Assert.True(d.CanLevel);
    }

    [AvaloniaFact]
    public async Task Level_is_off_with_a_reason_when_the_photo_has_no_usable_gravity()
    {
        await using var h = await OpenWhereAsync(l => !l.Available);
        var d = h.Develop();
        var s = h.Session();
        Assert.False(d.CanLevel);
        Assert.Equal(s.LevelSuggestion.Reason, d.LevelNote);
        Assert.False(string.IsNullOrWhiteSpace(d.LevelNote));
        int steps = s.History.Count;
        d.LevelCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.Equal(steps, s.History.Count);
        Assert.False(s.Geometry.HasLevel);
        h.DevelopView().FindControl<Button>("LevelButton")!.BringIntoView();
        await h.SettlePictureAsync();
        h.Shot("motion-04-develop-level-unavailable");
    }

    [AvaloniaFact]
    public async Task Level_is_off_while_the_crop_tool_is_open()
    {
        await using var h = await OpenWhereAsync(l => l.Available);
        var d = h.Develop();
        d.CropCommand.Execute(null);
        await h.SettlePictureAsync();
        Assert.True(d.IsCropping);
        Assert.False(d.CanLevel);
    }

    // ------------------------------------------------------------------------------------------------ the transform of the level rotation

    static (float C, float S) CosSin(double degrees)
    {
        double r = degrees * (3.14159265358979323846 / 180.0);
        return ((float)Lux.Engine.Pipeline.WineCrt.Cos(r), (float)Lux.Engine.Pipeline.WineCrt.Sin(r));
    }

    /// <summary>The four crop corners in the capture's 4 × 3 frame.</summary>
    static IEnumerable<(double U, double V)> Corners(LumenTransform t)
    {
        foreach (var (x, y) in new[] { (t.X0, t.Y0), (t.X1, t.Y0), (t.X0, t.Y1), (t.X1, t.Y1) })
        {
            double px = t.AspectW * (double)x, py = t.AspectH * (double)y;
            yield return (t.M[0] * px + t.M[3] * py + t.M[6], t.M[1] * px + t.M[4] * py + t.M[7]);
        }
    }

    [Theory]
    [InlineData(2.23)]
    [InlineData(-2.99)]
    [InlineData(10.0)]
    public void Level_of_an_untouched_photo_is_a_rotation_about_the_centre_with_the_largest_crop_of_the_same_shape(double degrees)
    {
        var g = new LumenGeometryEdits(LumenTransform.Identity(4, 3));
        var (c, s) = CosSin(degrees);
        g.Level(c, s);
        var m = g.T.M;
        // the rotation about the frame center (2, 1.5), output → capture
        Assert.Equal(c, m[0]); Assert.Equal(s, m[1]); Assert.Equal(-s, m[3]); Assert.Equal(c, m[4]);
        Assert.Equal(2 - (c * 2.0 - s * 1.5), m[6], 5);
        Assert.Equal(1.5 - (s * 2.0 + c * 1.5), m[7], 5);
        // the largest centered 4 : 3 rectangle that stays inside the frame
        double k = Math.Min(4 / (4 * Math.Abs(c) + 3 * Math.Abs(s)), 3 / (4 * Math.Abs(s) + 3 * Math.Abs(c)));
        Assert.Equal((1 - k) / 2, g.T.X0, 5); Assert.Equal((1 - k) / 2, g.T.Y0, 5);
        Assert.Equal((1 + k) / 2, g.T.X1, 5); Assert.Equal((1 + k) / 2, g.T.Y1, 5);
        Assert.All(Corners(g.T), p => { Assert.InRange(p.U, -1e-4, 4 + 1e-4); Assert.InRange(p.V, -1e-4, 3 + 1e-4); });
        Assert.Equal(degrees, LumenGeometryEdits.LevelDegrees(g.T), 4);
        Assert.Equal(0, PhotoEditImpl.Describe(g.T).TurnDegrees);
        Assert.Equal(degrees, PhotoEditImpl.Describe(g.T).LevelDegrees, 4);
    }

    [Fact]
    public void Turns_and_flips_keep_the_level_rotation_and_a_second_level_replaces_it()
    {
        var g = new LumenGeometryEdits(LumenTransform.Identity(4, 3));
        var (c, s) = CosSin(1.8);
        g.Level(c, s);
        var levelled = g.T.Clone();

        // four quarter turns: the same rotation bits at every turn, the crop inside the frame, and the start again at the end
        for (int i = 1; i <= 4; i++)
        {
            g.RotateRight();
            Assert.Equal((c, s), g.T.FineRotation());
            Assert.Equal(i % 4 * 90, LumenGeometryEdits.Orientation(g.T).Angle);
            Assert.All(Corners(g.T), p => { Assert.InRange(p.U, -1e-4, 4 + 1e-4); Assert.InRange(p.V, -1e-4, 3 + 1e-4); });
        }
        for (int i = 0; i < 9; i++) Assert.Equal(levelled.M[i], g.T.M[i], 5);
        Assert.Equal(levelled.X0, g.T.X0, 5); Assert.Equal(levelled.Y1, g.T.Y1, 5);

        // a flip mirrors the picture: the matrix rotation has the other direction, the content of the capture is still turned the same way
        g.MirrorHorizontal();
        Assert.Equal((c, -s), g.T.FineRotation());
        Assert.True(LumenGeometryEdits.Orientation(g.T).Mirrored);
        Assert.Equal(1.8, LumenGeometryEdits.LevelDegrees(g.T), 4);
        g.MirrorHorizontal();
        Assert.Equal((c, s), g.T.FineRotation());

        // the same suggestion again changes nothing; another angle replaces the first one
        var before = g.T.Clone();
        g.Level(c, s);
        Assert.True(g.T.SameAs(before));
        var (c2, s2) = CosSin(-0.7);
        g.Level(c2, s2);
        Assert.Equal((c2, s2), g.T.FineRotation());

        // without the level rotation, the turn and the crop stay
        g.RotateRight();
        var turned = g.T.Clone();
        LumenGeometryEdits.RemoveLevel(turned);
        Assert.False(turned.HasFineRotation);
        Assert.Equal(90, LumenGeometryEdits.Orientation(turned).Angle);
        Assert.Equal((g.T.X0, g.T.Y0, g.T.X1, g.T.Y1), (turned.X0, turned.Y0, turned.X1, turned.Y1));

        g.ResetGeometry();
        Assert.True(g.T.IsIdentity);
        Assert.False(g.T.HasFineRotation);
    }

    [Fact]
    public void A_small_crop_in_the_middle_keeps_its_size_when_the_photo_is_levelled()
    {
        var g = new LumenGeometryEdits(LumenTransform.Identity(4, 3));
        g.T.SetCrop(0.3f, 0.3f, 0.4f, 0.4f);
        g.InitTransform(g.T);
        var (c, s) = CosSin(3.0);
        g.Level(c, s);
        Assert.Equal(0.3f, g.T.X0); Assert.Equal(0.3f, g.T.Y0);
        Assert.Equal(0.4f, g.T.X1 - g.T.X0, 5);
        Assert.Equal((c, s), g.T.FineRotation());
    }

    // ------------------------------------------------------------------------------------------------ the level angle from gravity

    static Ltpb.LightHeader Header(params (double X, double Y, double Z)[] gravity)
    {
        var h = new Ltpb.LightHeader();
        var imu = new Ltpb.IMUData { FrameIndex = 0 };
        uint row = 0;
        foreach (var (x, y, z) in gravity)
            imu.Accelerometer.Add(new Ltpb.IMUData.Types.Sample { RowIdx = row += 150, Data = new Ltpb.Point3F { X = (float)x, Y = (float)y, Z = (float)z } });
        h.ImuData.Add(imu);
        return h;
    }

    static (double X, double Y, double Z) Gravity(double rollDegrees) => (Math.Sin(rollDegrees * Math.PI / 180), Math.Cos(rollDegrees * Math.PI / 180), 0.03);

    [Theory]
    [InlineData(2.0, 0, 2.0)]        // landscape
    [InlineData(-3.5, 0, -3.5)]
    [InlineData(-88.0, 90, 2.0)]     // portrait, turned 90° clockwise to be upright
    [InlineData(91.5, 270, 1.5)]
    [InlineData(178.0, 180, -2.0)]   // upside down
    public void Level_angle_is_the_gravity_angle_relative_to_the_held_turn_plus_the_mounting_offset(double roll, int held, double relative)
    {
        var header = Header(Gravity(roll), Gravity(roll), Gravity(roll));
        Assert.Equal(held, Lux.Store.HeldTurn.Degrees(header));
        var (degrees, state) = CaptureMotion.LevelOf(header, held);
        Assert.Equal(LevelState.Available, state);
        Assert.Equal(relative + CaptureMotion.MountingOffsetDegrees, degrees!.Value, 3);
        var suggestion = LevelSuggestion.From(degrees, state);
        Assert.True(suggestion.Available);
        Assert.Null(suggestion.Reason);
    }

    [Fact]
    public void Level_angle_is_not_given_without_usable_gravity()
    {
        // no samples
        var none = CaptureMotion.LevelOf(new Ltpb.LightHeader(), -1);
        Assert.Equal((null, LevelState.NoMotionData), none);
        // the camera points down: gravity is along the lens
        var down = Header((0.05, 0.1, 0.99), (0.05, 0.1, 0.99));
        Assert.Equal(-1, Lux.Store.HeldTurn.Degrees(down));
        Assert.Equal(LevelState.PointsUpOrDown, CaptureMotion.LevelOf(down, -1).State);
        // between landscape and portrait
        var between = Header(Gravity(35), Gravity(35));
        Assert.Equal(LevelState.BetweenLandscapeAndPortrait, CaptureMotion.LevelOf(between, Lux.Store.HeldTurn.Degrees(between)).State);
        foreach (var state in new[] { LevelState.NoMotionData, LevelState.PointsUpOrDown, LevelState.BetweenLandscapeAndPortrait })
        {
            var s = LevelSuggestion.From(null, state);
            Assert.False(s.Available);
            Assert.False(string.IsNullOrWhiteSpace(s.Reason));
        }
    }

    // ------------------------------------------------------------------------------------------------ the library database

    static string Fresh(string name)
    {
        string dir = Path.Combine(Paths.UiScratch, "motion", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static T Sql<T>(string db, string sql, Func<Microsoft.Data.Sqlite.SqliteCommand, T> run)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return run(cmd);
    }

    [Fact]
    public async Task A_library_of_the_previous_format_opens_and_keeps_its_data()
    {
        string root = Fresh("migrate");
        string db = Path.Combine(root, "library.luxlib");
        await using (var lib = LuxLibrary.Open(new LibraryOpenOptions { DatabasePath = db, StartWorkers = false, WatchFolders = false })) { }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        // the database as the previous version wrote it: format 5, no blur column, one rated capture read at header version 2
        Sql(db, """
            ALTER TABLE capture DROP COLUMN blur_px;
            PRAGMA user_version=5;
            UPDATE meta SET value=5 WHERE key='schema_version';
            INSERT INTO capture(id, primary_path, name, name_key, location_count, file_size, added_utc, has_meta, meta_version, rating, iso, exposure_s)
            VALUES('abc', '/photos/L16_00001.lri', 'L16_00001', 'l16_00001', 1, 123, 1, 1, 2, 4, 200, 0.01);
            """, c => c.ExecuteNonQuery());
        Assert.Equal(5L, Sql(db, "PRAGMA user_version", c => (long)c.ExecuteScalar()!));

        await using (var lib = LuxLibrary.Open(new LibraryOpenOptions { DatabasePath = db, StartWorkers = false, WatchFolders = false }))
        {
            var item = lib.Get("abc");
            Assert.NotNull(item);
            Assert.Equal(4, item!.Rating);
            Assert.Equal(200, item.Iso);
            Assert.Null(item.PredictedBlurPixels);           // not known until the next scan reads the header again
            Assert.False(item.PossiblyBlurred);
            Assert.Equal(0, lib.GetSmartFilterCounts()[SmartFilter.PossiblyBlurred]);
            Assert.Equal(1, lib.Count(new CaptureQuery { PossiblyBlurred = false }));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.Equal(6L, Sql(db, "PRAGMA user_version", c => (long)c.ExecuteScalar()!));
        Assert.Equal("6", Sql(db, "SELECT value FROM meta WHERE key='schema_version'", c => System.Convert.ToString(c.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)));
        // the header version is still the old one: the next scan reads this capture again
        Assert.Equal(2L, Sql(db, "SELECT meta_version FROM capture WHERE id='abc'", c => (long)c.ExecuteScalar()!));
    }

    // ------------------------------------------------------------------------------------------------ real captures

    /// <summary>Reference values for captures of the test folder: the predicted blur in pixels, and the level angle before the mounting
    /// offset (null = no level angle). They come from an independent computation on the same files.</summary>
    static readonly (string Name, double Blur, double? Roll)[] Reference =
    {
        ("L16_00305", 0.18, -1.068),    // steady, landscape
        ("L16_00448", 0.03, -2.568),
        ("L16_00409", 12.78, 0.874),    // a stack: the samples do not cover the exposure
        ("L16_00416", 8.80, 0.796),     // a stack, portrait
        ("L16_00423", 9.75, 5.286),     // portrait
        ("L16_00431", 20.35, null),     // the camera pointed down
        ("L16_00443", 13.42, -12.49),   // the gyroscope at its limit
        ("L16_00449", 9.11, -6.335),    // a long exposure
    };

    [Fact]
    public async Task Real_captures_get_the_reference_blur_and_level_and_an_older_library_reads_them_at_the_next_scan()
    {
        var present = Reference.Where(r => File.Exists(Path.Combine(Paths.Captures, r.Name + ".lri"))).ToList();
        if (Paths.Captures.Length == 0 || present.Count == 0) { _out.WriteLine("LUX_TEST_CAPTURES has none of the captures: skipped"); return; }
        string root = Fresh("real");
        string photos = Path.Combine(root, "photos");
        Directory.CreateDirectory(photos);
        foreach (var r in present) File.CreateSymbolicLink(Path.Combine(photos, r.Name + ".lri"), Path.Combine(Paths.Captures, r.Name + ".lri"));
        string db = Path.Combine(root, "lib", "library.luxlib");
        var options = new LibraryOpenOptions { DatabasePath = db, WatchFolders = false, MakeThumbnailsAfterScan = false };
        var blur = new Dictionary<string, float?>();
        long folderId;
        string levelId = "", ratedId = "";
        await using (var lib = LuxLibrary.Open(options))
        {
            lib.Settings.AnalyseNewCaptures = false;
            var folder = await lib.AddFolderAsync(photos);
            folderId = folder.Id;
            if (folder.ScanJobId is long scan) await lib.Jobs.WaitAsync(scan);
            var items = (await lib.QueryAsync(new CaptureQuery { Limit = 100 })).Items;
            Assert.Equal(present.Count, items.Count);
            foreach (var r in present)
            {
                var item = items.Single(i => i.Name == r.Name);
                var lri = LriFile.Load(item.Path);
                var (level, state) = CaptureMotion.LevelOf(lri.Header, Lux.Store.HeldTurn.Degrees(lri));
                _out.WriteLine($"{r.Name}: blur {item.PredictedBlurPixels:0.00} px (reference {r.Blur:0.00}), possibly blurred {item.PossiblyBlurred}, held {item.HeldOrientation}, "
                               + $"level {(level is float l ? l.ToString("0.000") : state.ToString())} (reference {(r.Roll is double x ? (x + CaptureMotion.MountingOffsetDegrees).ToString("0.000") : "none")})");
                Assert.NotNull(item.PredictedBlurPixels);
                Assert.InRange(Math.Abs(item.PredictedBlurPixels!.Value - r.Blur), 0, 0.02);
                Assert.Equal(r.Blur > CaptureItem.PossiblyBlurredPixels, item.PossiblyBlurred);
                if (r.Roll is double roll) { Assert.Equal(LevelState.Available, state); Assert.InRange(Math.Abs(level!.Value - (roll + CaptureMotion.MountingOffsetDegrees)), 0, 0.002); }
                else Assert.NotEqual(LevelState.Available, state);
                blur[item.Id] = item.PredictedBlurPixels;
            }
            Assert.Equal(present.Count(r => r.Blur > 5), lib.GetSmartFilterCounts()[SmartFilter.PossiblyBlurred]);
            Assert.Equal(present.Count(r => r.Blur > 5), lib.Count(new CaptureQuery().With(SmartFilter.PossiblyBlurred)));
            ratedId = items[0].Id;
            await lib.SetRatingAsync(ratedId, 3);
            levelId = items.OrderByDescending(i => i.Name == "L16_00448").First(i => present.First(r => r.Name == i.Name).Roll is not null && i.HeldOrientation == PhotoOrientation.Normal).Id;
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // the same library as an older version left it: the headers read at version 2, no blur values
        Sql(db, "UPDATE capture SET meta_version=2, blur_px=NULL", c => c.ExecuteNonQuery());
        await using (var lib = LuxLibrary.Open(options))
        {
            lib.Settings.AnalyseNewCaptures = false;
            Assert.All((await lib.QueryAsync(new CaptureQuery { Limit = 100 })).Items, i => Assert.Null(i.PredictedBlurPixels));
            await lib.Jobs.WaitAsync(lib.RescanFolder(folderId));
            var items = (await lib.QueryAsync(new CaptureQuery { Limit = 100 })).Items;
            Assert.Equal(present.Count, items.Count);
            Assert.All(items, i => Assert.Equal(blur[i.Id], i.PredictedBlurPixels));   // the same bits as the first scan
            Assert.Equal(3, items.Single(i => i.Id == ratedId).Rating);

            // Level on a real photo: the suggestion of the capture, one step, saved in the photo transform, undone by undo
            var item = lib.Get(levelId)!;
            double expected = present.First(r => r.Name == item.Name).Roll!.Value + CaptureMotion.MountingOffsetDegrees;
            await using var photo = await lib.OpenPhotoAsync(levelId);
            var edit = photo.Edit;
            Assert.True(edit.LevelSuggestion.Available);
            Assert.InRange(Math.Abs(edit.LevelSuggestion.Degrees - expected), 0, 0.002);
            Assert.True(edit.Transform.IsIdentity);
            edit.Level();
            var t = edit.Transform;
            _out.WriteLine($"{item.Name}: level {edit.LevelSuggestion.Degrees:0.000}°, matrix [{string.Join(' ', t.Matrix.Select(v => v.ToString("0.00000")))}], crop [{t.CropX0:0.00000} {t.CropY0:0.00000} {t.CropX1:0.00000} {t.CropY1:0.00000}]");
            Assert.Equal(edit.LevelSuggestion.Degrees, t.LevelDegrees, 3);
            var (c, s) = CosSin(edit.LevelSuggestion.Degrees);
            Assert.Equal(c, t.Matrix[0]); Assert.Equal(s, t.Matrix[1]);
            Assert.True(t.CropX0 > 0 && t.CropX1 < 1);
            Assert.True(edit.CanUndo);
            Assert.Equal("Undo level", edit.UndoLabel);
            var size = photo.Edit.Info.FullSize;
            Assert.True(size.Width < item.Width && size.Height < item.Height);

            // the viewer's picture of the leveled photo: the level-4 picture has the new size and no empty corner
            var geometry = photo.View.Geometry;
            var tiles = new System.Collections.Concurrent.ConcurrentBag<PictureTile>();
            photo.View.TileReady += (_, tile) => { if (tile.Id.Level == 4 && tile.Mode == ViewMode.Edited && tile.GeometryVersion == geometry.Version) tiles.Add(tile); };
            photo.View.RequestViewport(new ViewportRequest { Visible = NormRect.Full, ScreenWidth = 400, ScreenHeight = 300, Level = 4 });
            var (cols, rows) = geometry.Grid(4);
            for (int wait = 0; wait < 600 && tiles.Select(tile => tile.Id).Distinct().Count() < cols * rows; wait++) await Task.Delay(100);
            var size4 = geometry.LevelSizes[4];
            _out.WriteLine($"viewer level 4: {size4.Width} × {size4.Height} in {tiles.Select(tile => tile.Id).Distinct().Count()} tiles (not leveled: {item.LevelSizes[4].Width} × {item.LevelSizes[4].Height})");
            Assert.Equal(cols * rows, tiles.Select(tile => tile.Id).Distinct().Count());
            Assert.True(size4.Width < item.LevelSizes[4].Width);
            foreach (var (x, y) in new[] { (0, 0), (size4.Width - 1, 0), (0, size4.Height - 1), (size4.Width - 1, size4.Height - 1) })
            {
                var tile = tiles.First(t0 => x >= t0.Rect.X && x < t0.Rect.X + t0.Rect.Width && y >= t0.Rect.Y && y < t0.Rect.Y + t0.Rect.Height);
                int at = ((y - tile.Rect.Y) * tile.Rect.Width + (x - tile.Rect.X)) * 4;
                Assert.True(tile.Pixels[at] + tile.Pixels[at + 1] + tile.Pixels[at + 2] > 0, $"the corner {x}, {y} of the viewer's picture is empty");
            }

            // the saved level rotation reaches an export: a smaller picture of the same shape, with no empty corner
            await edit.SaveAsync();
            var job = await lib.Jobs.WaitAsync(lib.Export(new[] { levelId }, new ExportOptions { Formats = new[] { "jpg" }, Level = 3, OutputFolder = Path.Combine(root, "levelled"), UseCache = false }).Single());
            Assert.True(job.State == JobState.Done, job.Error);
            using (var bitmap = SkiaSharp.SKBitmap.Decode(Path.Combine(root, "levelled", item.Name + ".jpg")))
            {
                _out.WriteLine($"leveled export {bitmap.Width} × {bitmap.Height}");
                Assert.Equal(4.0 / 3.0, (double)bitmap.Width / bitmap.Height, 2);
                Assert.True(bitmap.Width < item.LevelSizes[3].Width);
                foreach (var (x, y) in new[] { (0, 0), (bitmap.Width - 1, 0), (0, bitmap.Height - 1), (bitmap.Width - 1, bitmap.Height - 1) })
                {
                    var p = bitmap.GetPixel(x, y);
                    Assert.True(p.Red + p.Green + p.Blue > 0, $"the corner {x}, {y} is empty");
                }
            }
            edit.Undo();
            Assert.True(edit.Transform.IsIdentity);
            Assert.Equal(0, edit.Transform.LevelDegrees);
        }
    }
}
