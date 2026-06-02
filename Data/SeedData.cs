using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ZenRead.Entities;

namespace ZenRead.Data;

public static class SeedData
{
    private const string DefaultCoverUrl = "/images/book-cover-thinking-fast-and-slow.svg";

    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var scopedServices = scope.ServiceProvider;
        var dbContext = scopedServices.GetRequiredService<ApplicationDbContext>();

        await dbContext.Database.MigrateAsync();
        await SeedRolesAsync(scopedServices);
        await SeedAdminUserAsync(scopedServices);
        await SeedDemoAdminUserAsync(scopedServices);
        await SeedCuratedBooksAsync(dbContext);
    }

    private static async Task SeedRolesAsync(IServiceProvider services)
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();

        foreach (var role in new[] { "Admin", "Reader" })
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }
    }

    private static async Task SeedAdminUserAsync(IServiceProvider services)
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var configuration = services.GetRequiredService<IConfiguration>();

        var email = configuration["SeedAdmin:Email"];
        var password = configuration["SeedAdmin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var fullName = configuration["SeedAdmin:FullName"] ?? "LemonInk Admin";

        var admin = await userManager.FindByEmailAsync(email);
        if (admin is null)
        {
            admin = new ApplicationUser
            {
                FullName = fullName,
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
                LastLoginAt = DateTime.UtcNow
            };

            var createResult = await userManager.CreateAsync(admin, password);
            if (!createResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Không tạo được tài khoản admin seed: {string.Join(" ", createResult.Errors.Select(error => error.Description))}");
            }
        }
        else if (!admin.EmailConfirmed)
        {
            admin.EmailConfirmed = true;
            await userManager.UpdateAsync(admin);
        }

        if (!await userManager.IsInRoleAsync(admin, "Admin"))
        {
            await userManager.AddToRoleAsync(admin, "Admin");
        }
    }

    private static async Task SeedDemoAdminUserAsync(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var environment = services.GetRequiredService<IWebHostEnvironment>();
        if (!configuration.GetValue("SeedDemoAdmin:Enabled", false))
        {
            return;
        }

        if (!environment.IsDevelopment())
        {
            return;
        }

        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

        var email = configuration["SeedDemoAdmin:Email"] ?? "demo.admin@lemonink.local";
        var password = configuration["SeedDemoAdmin:Password"] ?? "LemonInkDemo@2026!";
        var fullName = configuration["SeedDemoAdmin:FullName"] ?? "LemonInk Demo Admin";

        var demoAdmin = await userManager.FindByEmailAsync(email);
        if (demoAdmin is null)
        {
            demoAdmin = new ApplicationUser
            {
                FullName = fullName,
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
                LastLoginAt = DateTime.UtcNow
            };

            var createResult = await userManager.CreateAsync(demoAdmin, password);
            if (!createResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Không tạo được tài khoản demo admin seed: {string.Join(" ", createResult.Errors.Select(error => error.Description))}");
            }
        }
        else
        {
            var needsUpdate = false;
            if (!demoAdmin.EmailConfirmed)
            {
                demoAdmin.EmailConfirmed = true;
                needsUpdate = true;
            }

            if (string.IsNullOrWhiteSpace(demoAdmin.FullName))
            {
                demoAdmin.FullName = fullName;
                needsUpdate = true;
            }

            if (needsUpdate)
            {
                await userManager.UpdateAsync(demoAdmin);
            }

            if (!await userManager.CheckPasswordAsync(demoAdmin, password))
            {
                if (await userManager.HasPasswordAsync(demoAdmin))
                {
                    var removePasswordResult = await userManager.RemovePasswordAsync(demoAdmin);
                    if (!removePasswordResult.Succeeded)
                    {
                        throw new InvalidOperationException(
                            $"Không reset được mật khẩu demo admin seed: {string.Join(" ", removePasswordResult.Errors.Select(error => error.Description))}");
                    }
                }

                var addPasswordResult = await userManager.AddPasswordAsync(demoAdmin, password);
                if (!addPasswordResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Không đặt được mật khẩu demo admin seed: {string.Join(" ", addPasswordResult.Errors.Select(error => error.Description))}");
                }
            }
        }

        if (!await userManager.IsInRoleAsync(demoAdmin, "Admin"))
        {
            await userManager.AddToRoleAsync(demoAdmin, "Admin");
        }
    }

    private static async Task SeedCuratedBooksAsync(ApplicationDbContext dbContext)
    {
        var existingBooks = await dbContext.Books
            .Include(book => book.GeneratedSummaries)
            .Include(book => book.SummarySections)
            .Include(book => book.Takeaways)
            .Include(book => book.Audios)
            .Include(book => book.ContentChunks)
            .Where(book => book.SourceType == BookSourceType.Curated)
            .ToListAsync();

        var curatedBooks = CuratedBooks;

        foreach (var seedBook in curatedBooks)
        {
            var book = existingBooks.FirstOrDefault(existing => existing.Slug == seedBook.Slug);

            if (book is null)
            {
                book = new Book { SourceType = BookSourceType.Curated };
                dbContext.Books.Add(book);
            }
            else if (book.GeneratedSummaries.Any(summary => summary.EditedByAdmin))
            {
                continue;
            }
            else
            {
                var fakeSeedAudios = book.Audios
                    .Where(IsSeedAudio)
                    .ToList();

                dbContext.GeneratedBookSummaries.RemoveRange(book.GeneratedSummaries);
                dbContext.BookSummarySections.RemoveRange(book.SummarySections);
                dbContext.BookTakeaways.RemoveRange(book.Takeaways);
                dbContext.BookAudios.RemoveRange(fakeSeedAudios);
            }

            var readyAudio = book.Audios
                .Where(audio => !IsSeedAudio(audio) && audio.Status == AudioStatus.Ready)
                .OrderByDescending(audio => audio.CreatedAt)
                .FirstOrDefault();

            book.Title = seedBook.Title;
            book.Slug = seedBook.Slug;
            book.AuthorName = seedBook.Author;
            book.Description = seedBook.Description;
            book.Introduction = seedBook.Introduction;
            book.CoverUrl = seedBook.CoverUrl;
            book.Category = seedBook.Category;
            book.CoverGradient = seedBook.CoverGradient;
            book.CoverSvg = seedBook.CoverSvg;
            book.Visibility = BookVisibility.Public;
            book.ProcessingStatus = readyAudio is null ? BookProcessingStatus.SummaryReady : BookProcessingStatus.Ready;
            book.Rating = seedBook.Rating;
            book.ReadingTimeMinutes = seedBook.ReadingTimeMinutes;
            book.AudioDurationSeconds = readyAudio?.DurationSeconds;
            book.IsSummaryReady = true;
            book.IsAudioReady = readyAudio is not null;
            book.PublishedAt ??= DateTime.UtcNow;
            book.UpdatedAt = DateTime.UtcNow;

            book.GeneratedSummaries = new List<GeneratedBookSummary>
            {
                new()
                {
                    ShortSummary = seedBook.Description,
                    LongSummary = seedBook.Introduction,
                    GeneratedBy = "LemonInk seed",
                    UpdatedAt = DateTime.UtcNow
                }
            };

            book.Takeaways = seedBook.Takeaways
                .Select((content, index) => new BookTakeaway
                {
                    Content = content,
                    SortOrder = index + 1
                })
                .ToList();

            book.SummarySections = seedBook.Chapters
                .Select(chapter => new BookSummarySection
                {
                    SectionType = SummarySectionType.Chapter,
                    ChapterNumber = chapter.Number,
                    Title = chapter.Title,
                    ContentHtml = chapter.ContentHtml,
                    ReadingTimeMinutes = chapter.ReadingTimeMinutes,
                    SortOrder = chapter.Number,
                    UpdatedAt = DateTime.UtcNow
                })
                .ToList();

            if (book.ContentChunks.Count == 0)
            {
                book.ContentChunks = seedBook.Chapters
                    .Select((chapter, index) => new BookContentChunk
                    {
                        ChunkIndex = index,
                        Content = StripHtml(chapter.ContentHtml),
                        TokenCount = EstimateTokenCount(chapter.ContentHtml),
                        CreatedAt = DateTime.UtcNow
                    })
                    .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Content))
                    .ToList();
            }
        }

        await dbContext.SaveChangesAsync();
    }

    private static bool IsSeedAudio(BookAudio audio)
    {
        return audio.Provider?.Equals("seed", StringComparison.OrdinalIgnoreCase) == true ||
            audio.AudioUrl.StartsWith("/audio/", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripHtml(string value)
    {
        return WebUtility.HtmlDecode(Regex.Replace(value, "<.*?>", " "))
            .Replace("&nbsp;", " ")
            .Trim();
    }

    private static int EstimateTokenCount(string value)
    {
        return Math.Max(1, value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length);
    }

    private static List<SeedBook> CuratedBooks =>
    [
        Book(
            "Tư Duy Nhanh và Chậm",
            "tu-duy-nhanh-va-cham",
            "Daniel Kahneman",
            "Tâm lý học",
            9.50m,
            28,
            "linear-gradient(135deg, #667eea 0%, #764ba2 100%)",
            LightbulbSvg,
            "Kahneman giải thích vì sao con người phán đoán rất nhanh nhưng cũng thường sai theo những mẫu lặp lại.",
            "Cuốn sách đưa người đọc vào cơ chế vận hành của hai hệ thống tư duy: một hệ thống nhanh, trực giác và tự động; một hệ thống chậm, phân tích và tốn nỗ lực. Điểm mạnh của sách nằm ở cách nó biến các sai lầm tư duy hằng ngày thành những hiện tượng có tên gọi, có quy luật và có thể quan sát được trong công việc, tài chính, y tế, quản trị và đời sống cá nhân.",
            [
                "Tư duy nhanh giúp phản ứng kịp thời nhưng thường tạo ra câu trả lời quá tự tin.",
                "Tư duy chậm chính xác hơn trong vấn đề phức tạp nhưng dễ lười biếng và bị tư duy nhanh dẫn dắt.",
                "Thiên kiến nhận thức không phải lỗi cá nhân hiếm gặp mà là mẫu sai lệch phổ biến của não bộ.",
                "Những quyết định lớn nên dựa vào quy trình kiểm tra, dữ liệu và tỷ lệ nền thay vì chỉ dựa cảm giác.",
                "Muốn ra quyết định tốt hơn, hãy thiết kế môi trường buộc bản thân chậm lại đúng lúc."
            ],
            [
                Chapter(1, "Hai Hệ Thống Tư Duy", 5,
                    "Kahneman mô tả Hệ Thống 1 như phần tâm trí phản ứng tức thì: nhận ra gương mặt tức giận, hiểu một câu đơn giản, hoặc phanh lại khi thấy nguy hiểm. Nó hoạt động liên tục, không cần mệnh lệnh rõ ràng và tạo cho ta cảm giác rằng mình luôn có nhận định về thế giới.",
                    "Hệ Thống 2 thì khác. Nó xuất hiện khi bạn làm phép nhân khó, đọc một hợp đồng quan trọng, so sánh hai lựa chọn đầu tư hoặc cố gắng kiềm chế một phản ứng bốc đồng. Nó chậm hơn, tốn năng lượng hơn, và vì vậy thường chỉ can thiệp khi vấn đề thật sự đòi hỏi.",
                    "Vấn đề cốt lõi là Hệ Thống 2 thường tin những gợi ý mà Hệ Thống 1 đưa lên. Trong nhiều tình huống, sự hợp tác này giúp đời sống trôi chảy. Nhưng trong các vấn đề có xác suất, mâu thuẫn lợi ích hoặc thông tin thiếu rõ ràng, nó khiến ta trả lời nhanh cho một câu hỏi lẽ ra cần được suy nghĩ chậm."),
                Chapter(2, "Thiên Kiến Nhận Thức", 6,
                    "Thiên kiến nhận thức là những lỗi tư duy có hệ thống, không phải vài lần nhầm lẫn ngẫu nhiên. Não bộ dùng lối tắt để tiết kiệm năng lượng, và những lối tắt này thường hữu ích trong đời sống thường ngày. Nhưng khi được đưa vào quyết định tài chính, tuyển dụng hay y tế, chúng có thể tạo ra hậu quả lớn.",
                    "Một ví dụ là thiên kiến có sẵn: ta đánh giá khả năng xảy ra của một sự kiện dựa trên mức độ dễ nhớ của ví dụ. Nếu vừa xem tin về tai nạn máy bay, ta có thể cảm thấy bay nguy hiểm hơn, dù số liệu thực tế không đổi. Tâm trí nhầm độ sống động của ký ức với xác suất.",
                    "Cuốn sách không hứa rằng chỉ cần biết tên thiên kiến là tránh được nó. Kahneman thực tế hơn: biết thiên kiến giúp ta xây quy trình phòng vệ. Những quy trình như kiểm tra tỷ lệ nền, hỏi ý kiến độc lập và viết trước giả định có thể làm giảm sức kéo của trực giác."),
                Chapter(3, "Hiệu Ứng Neo", 5,
                    "Hiệu ứng neo cho thấy thông tin đầu tiên xuất hiện có thể kéo lệch phán đoán về sau. Trong thương lượng giá, con số mở đầu thường đặt ra khung tham chiếu cho toàn bộ cuộc trò chuyện. Ngay cả khi biết con số ấy tùy tiện, tâm trí vẫn có xu hướng điều chỉnh từ nó thay vì bắt đầu hoàn toàn độc lập.",
                    "Hiệu ứng này đáng sợ vì nó xuất hiện cả khi neo không liên quan. Một con số ngẫu nhiên, một giá niêm yết, hoặc một dự báo đầu tiên đều có thể ảnh hưởng đến ước lượng sau đó. Điều này giải thích vì sao những quyết định tưởng là khách quan vẫn bị bối cảnh trình bày chi phối.",
                    "Cách chống lại neo là tạo nhiều điểm tham chiếu khác nhau. Thay vì hỏi một mức giá có hợp lý không, hãy khảo sát nhiều nguồn, tự ước lượng trước khi nhìn đề xuất và tách người đánh giá khỏi thông tin có thể gây nhiễu."),
                Chapter(4, "Tự Tin Thái Quá", 6,
                    "Kahneman cho rằng con người rất giỏi kể chuyện về quá khứ, rồi nhầm khả năng kể chuyện ấy với khả năng dự đoán tương lai. Khi một sự kiện đã xảy ra, ta dễ thấy nó như điều tất yếu. Cảm giác tất yếu này khiến ta đánh giá thấp bất định và đánh giá cao năng lực phán đoán của mình.",
                    "Trong kinh doanh, tự tin thái quá khiến nhóm dự án bỏ qua tỷ lệ thất bại của các dự án tương tự. Trong đầu tư, nó khiến người ta tin mình nhìn thấy cơ hội mà thị trường chưa thấy. Trong đời sống cá nhân, nó khiến kế hoạch luôn lạc quan hơn nguồn lực thật.",
                    "Một kỹ thuật hữu ích là nhìn từ bên ngoài: thay vì chỉ phân tích dự án của mình, hãy hỏi những dự án cùng loại thường kết thúc ra sao. Dữ liệu bên ngoài làm giảm sức mạnh của câu chuyện nội bộ, nhất là khi nhóm đang quá hào hứng với ý tưởng của mình."),
                Chapter(5, "Ra Quyết Định Tốt Hơn", 6,
                    "Cuốn sách khép lại bằng một tinh thần khiêm tốn: ta không thể loại bỏ hoàn toàn lỗi tư duy, nhưng có thể thiết kế cách ra quyết định để bớt phụ thuộc vào khoảnh khắc cảm xúc. Điều này đặc biệt quan trọng với các quyết định lặp lại hoặc có rủi ro cao.",
                    "Pre-mortem là một ví dụ: trước khi bắt đầu dự án, hãy tưởng tượng nó đã thất bại và viết ra lý do. Bài tập này cho phép người trong nhóm nói về rủi ro mà không bị xem là bi quan. Nó biến phản biện thành một phần chính thức của quy trình.",
                    "Bài học thực tế nhất là hãy dùng trực giác ở nơi nó có kinh nghiệm và phản hồi tốt, nhưng hãy dùng quy trình ở nơi trực giác dễ bị đánh lừa. Người khôn ngoan không phải người luôn nghĩ chậm, mà là người biết khi nào cần chậm lại.")
            ]),
        Book(
            "Sapiens: Lược Sử Loài Người",
            "sapiens-luoc-su-loai-nguoi",
            "Yuval Noah Harari",
            "Lịch sử",
            9.25m,
            31,
            "linear-gradient(135deg, #f093fb 0%, #f5576c 100%)",
            GlobeSvg,
            "Harari kể lại hành trình của Homo sapiens từ một loài không nổi bật đến lực lượng định hình hành tinh.",
            "Sapiens nhìn lịch sử loài người qua ba cuộc cách mạng lớn: nhận thức, nông nghiệp và khoa học. Thay vì kể lịch sử theo từng triều đại, sách đặt câu hỏi vì sao loài người có thể hợp tác ở quy mô khổng lồ, vì sao những câu chuyện chung như tiền, quốc gia và luật pháp có sức mạnh, và cái giá mà tiến bộ để lại cho cá nhân, xã hội lẫn các loài khác.",
            [
                "Homo sapiens vượt lên nhờ khả năng cùng tin vào những câu chuyện chung.",
                "Nông nghiệp tạo xã hội lớn hơn nhưng không chắc làm đời sống cá nhân dễ chịu hơn.",
                "Tiền, quốc gia và công ty là trật tự tưởng tượng có sức mạnh thực tế.",
                "Khoa học hiện đại phát triển khi con người dám thừa nhận mình chưa biết.",
                "Tiến bộ kỹ thuật luôn kéo theo câu hỏi về hạnh phúc, đạo đức và quyền lực."
            ],
            [
                Chapter(1, "Cách Mạng Nhận Thức", 6,
                    "Harari cho rằng bước ngoặt lớn đầu tiên không nằm ở cơ bắp hay công cụ, mà ở khả năng tưởng tượng tập thể. Homo sapiens có thể kể những câu chuyện vượt ra ngoài điều mắt thấy: thần linh, bộ tộc, luật lệ, danh dự, tiền bạc và lời hứa.",
                    "Những câu chuyện này cho phép người xa lạ hợp tác. Một nhóm nhỏ có thể vận hành bằng quan hệ trực tiếp, nhưng hàng nghìn hay hàng triệu người cần một niềm tin chung để phối hợp. Đây là nền tảng cho thương mại, quân đội, tôn giáo và nhà nước.",
                    "Điểm thú vị là các câu chuyện tưởng tượng không hề yếu. Một tờ tiền không có giá trị sinh học, nhưng nếu cả xã hội tin vào nó, nó có thể khiến con người lao động, di chuyển, chiến đấu và thay đổi đời sống."),
                Chapter(2, "Cách Mạng Nông Nghiệp", 6,
                    "Nông nghiệp thường được kể như một chiến thắng: con người thuần hóa cây trồng, ổn định nguồn lương thực và xây dựng làng mạc. Harari làm phức tạp câu chuyện này bằng cách hỏi liệu cá nhân nông dân có thật sự sống tốt hơn người săn bắt hái lượm hay không.",
                    "Định cư giúp dân số tăng nhưng cũng tạo ra bệnh tật, lao động nặng nhọc, chế độ ăn kém đa dạng và phụ thuộc mùa vụ. Khi đã đầu tư vào ruộng đất, con người khó quay lại đời sống cũ. Sự tiến bộ của loài có thể đi kèm sự vất vả của từng cá nhân.",
                    "Chương này hữu ích vì nó dạy ta nhìn tiến bộ như một thỏa thuận, không phải món quà miễn phí. Một hệ thống có thể làm xã hội mạnh hơn nhưng đồng thời khiến con người bên trong nó phải chịu áp lực mới."),
                Chapter(3, "Trật Tự Tưởng Tượng", 6,
                    "Luật pháp, quyền sở hữu, biên giới và công ty không tồn tại như đá hay cây, nhưng chúng tồn tại trong niềm tin tập thể. Khi đủ người tin vào chúng, chúng điều phối hành vi thật: ai được sở hữu đất, ai được vay tiền, ai có quyền ra lệnh.",
                    "Harari không phủ nhận giá trị của trật tự tưởng tượng. Nếu thiếu chúng, xã hội lớn khó vận hành. Nhưng ông nhắc rằng vì chúng do con người tạo ra, chúng có thể phục vụ lợi ích không đều. Một câu chuyện chung vừa có thể tạo hợp tác, vừa có thể hợp thức hóa bất bình đẳng.",
                    "Đọc chương này giúp người đọc bớt xem các thiết chế quen thuộc là tự nhiên. Nhiều điều tưởng hiển nhiên thực ra là kết quả của lịch sử, thỏa thuận và quyền lực."),
                Chapter(4, "Khoa Học, Đế Chế và Tư Bản", 7,
                    "Theo Harari, khoa học hiện đại bắt đầu bằng một thái độ khác thường: thừa nhận mình không biết. Chính sự thiếu biết ấy tạo động lực đo lường, thử nghiệm, ghi chép và sửa sai. Khi được kết hợp với tham vọng chính trị và vốn đầu tư, khoa học tăng tốc rất nhanh.",
                    "Đế chế tài trợ thám hiểm để có bản đồ, tài nguyên và quyền lực. Tư bản tài trợ phát minh vì kỳ vọng lợi nhuận tương lai. Khoa học vì vậy không đứng ngoài xã hội; nó phát triển trong mạng lưới lợi ích, tham vọng và niềm tin vào tăng trưởng.",
                    "Chương này cho thấy sức mạnh hiện đại không đến từ một yếu tố duy nhất. Tri thức, tiền bạc và quyền lực thường nâng đỡ nhau. Câu hỏi đạo đức là những năng lực mới ấy phục vụ ai và ai phải trả giá."),
                Chapter(5, "Tương Lai Của Sapiens", 6,
                    "Ở phần cuối, Harari chuyển từ lịch sử sang câu hỏi về tương lai. Nếu con người đã có khả năng thay đổi môi trường, gen và trí tuệ nhân tạo, ranh giới giữa tiến hóa tự nhiên và thiết kế có chủ đích bắt đầu mờ đi.",
                    "Cuốn sách không đưa ra một câu trả lời đơn giản về tương lai. Nó buộc người đọc hỏi: điều gì thật sự làm con người hạnh phúc, và liệu quyền lực lớn hơn có đồng nghĩa với đời sống tốt hơn không.",
                    "Thông điệp mạnh nhất là lịch sử không tất yếu. Những trật tự hôm nay từng được tạo ra, nên tương lai cũng sẽ được tạo ra bằng lựa chọn của con người, dù không phải lúc nào con người hiểu hết hậu quả.")
            ]),
        Book(
            "Atomic Habits",
            "atomic-habits",
            "James Clear",
            "Tự lực",
            9.00m,
            24,
            "linear-gradient(135deg, #4facfe 0%, #00f2fe 100%)",
            BoltSvg,
            "James Clear chỉ ra cách những thay đổi nhỏ, nếu được lặp lại đều, có thể tạo ra kết quả lớn.",
            "Atomic Habits là cuốn sách thực tế về việc xây hệ thống hành vi. Thay vì dựa vào cảm hứng nhất thời, Clear phân tích thói quen như một vòng lặp có thể thiết kế: tín hiệu, ham muốn, phản hồi và phần thưởng. Sách đặc biệt hữu ích vì biến mục tiêu lớn thành những hành động nhỏ đủ dễ để lặp lại.",
            [
                "Thói quen nhỏ tạo khác biệt lớn khi được lặp lại đủ lâu.",
                "Mục tiêu cho hướng đi, nhưng hệ thống quyết định kết quả hằng ngày.",
                "Muốn xây thói quen tốt, hãy làm nó rõ ràng, hấp dẫn, dễ làm và thỏa mãn.",
                "Môi trường tốt giảm nhu cầu dùng ý chí.",
                "Danh tính mới được củng cố bằng bằng chứng hành động nhỏ."
            ],
            [
                Chapter(1, "Tích Lũy Nhỏ và Kết Quả Lớn", 5,
                    "Clear mở đầu bằng một ý tưởng đơn giản: cải thiện 1% mỗi ngày gần như không đáng chú ý trong ngắn hạn, nhưng có sức mạnh lớn khi cộng dồn. Vấn đề là con người thường bỏ cuộc vì kết quả ban đầu quá chậm.",
                    "Ông gọi giai đoạn chưa thấy kết quả là thung lũng thất vọng. Bạn tập luyện, đọc sách, tiết kiệm hoặc viết mỗi ngày nhưng cuộc sống chưa thay đổi ngay. Nhiều người tưởng hệ thống không hiệu quả, trong khi thực ra năng lượng đang tích lũy.",
                    "Bài học thực tế là đừng đo thói quen chỉ bằng kết quả tức thì. Hãy đo bằng việc bạn có đang duy trì hệ thống đúng hướng không."),
                Chapter(2, "Bốn Quy Luật Của Thói Quen", 6,
                    "Mỗi thói quen bắt đầu bằng một tín hiệu, tạo ra ham muốn, dẫn đến phản hồi và kết thúc bằng phần thưởng. Nếu muốn hình thành thói quen tốt, hãy thiết kế từng mắt xích trong vòng lặp này.",
                    "Làm cho tín hiệu rõ ràng nghĩa là đặt hành vi vào lịch hoặc môi trường dễ thấy. Làm cho nó hấp dẫn nghĩa là ghép hành vi với điều bạn thích. Làm cho nó dễ thực hiện nghĩa là giảm ma sát đến mức hành động đầu tiên gần như không cần tranh đấu.",
                    "Phần thưởng giúp não muốn lặp lại hành vi. Vì nhiều thói quen tốt có lợi ích chậm, Clear khuyên tạo phần thưởng ngắn hạn lành mạnh để hành vi có cảm giác hoàn thành ngay."),
                Chapter(3, "Môi Trường Thắng Ý Chí", 6,
                    "Một trong những điểm mạnh của sách là nhấn mạnh môi trường. Nếu đồ ăn vặt nằm trên bàn, điện thoại nằm cạnh tay và ứng dụng giải trí luôn mở sẵn, ý chí sẽ phải chiến đấu liên tục. Chiến đấu liên tục là chiến lược dễ thua.",
                    "Thiết kế môi trường nghĩa là làm hành vi tốt trở nên mặc định. Đặt sách trên gối, chuẩn bị sẵn quần áo tập, khóa ứng dụng gây nhiễu trong giờ làm việc, hoặc để nước lọc trước mặt đều là cách giảm ma sát.",
                    "Điều này giải phóng người đọc khỏi cảm giác mình yếu đuối. Vấn đề không chỉ là cá tính, mà là thiết kế hệ thống xung quanh hành vi."),
                Chapter(4, "Danh Tính và Sự Bền Vững", 7,
                    "Clear cho rằng thay đổi sâu nhất là thay đổi danh tính. Thay vì chỉ đặt mục tiêu viết một bài, hãy trở thành người viết. Thay vì chỉ muốn chạy 5km, hãy trở thành người không bỏ lỡ buổi tập.",
                    "Danh tính không đến từ lời tuyên bố lớn mà từ bằng chứng nhỏ. Mỗi lần bạn làm một hành động phù hợp, bạn bỏ một phiếu cho con người mình muốn trở thành. Không cần hoàn hảo, chỉ cần đủ phiếu theo thời gian.",
                    "Cách nhìn này khiến thói quen bớt nặng nề. Một lần thất bại không phá hủy danh tính nếu bạn quay lại nhanh. Điều quan trọng là không để hai lần bỏ lỡ liên tiếp trở thành danh tính mới.")
            ]),
        Book(
            "Đắc Nhân Tâm",
            "dac-nhan-tam",
            "Dale Carnegie",
            "Tự lực",
            8.75m,
            22,
            "linear-gradient(135deg, #43e97b 0%, #38f9d7 100%)",
            UsersSvg,
            "Carnegie trình bày những nguyên tắc giao tiếp bền vững về lắng nghe, tôn trọng và ảnh hưởng tích cực.",
            "Đắc Nhân Tâm được đọc rộng rãi vì chạm vào một nhu cầu rất đời thường: làm sao sống và làm việc với người khác tốt hơn. Sách không nên được hiểu như bộ mẹo thao túng, mà như lời nhắc rằng con người muốn được tôn trọng, được lắng nghe và được giữ thể diện.",
            [
                "Chỉ trích trực diện thường tạo phòng thủ hơn là thay đổi.",
                "Sự quan tâm chân thành có sức mạnh hơn lời nói khéo.",
                "Lắng nghe tốt giúp người khác cảm thấy mình quan trọng.",
                "Thuyết phục hiệu quả bắt đầu từ điểm đồng thuận và lợi ích của người nghe.",
                "Giữ thể diện cho người khác giúp quan hệ bền hơn sau góp ý."
            ],
            [
                Chapter(1, "Đừng Bắt Đầu Bằng Chỉ Trích", 5,
                    "Carnegie mở đầu bằng quan sát rằng con người hiếm khi tự xem mình là kẻ sai hoàn toàn. Khi bị chỉ trích, phản ứng tự nhiên là phòng thủ, biện minh hoặc phản công. Vì vậy một lời góp ý đúng nhưng đưa ra sai cách vẫn có thể thất bại.",
                    "Sách khuyên bắt đầu bằng sự hiểu biết về động cơ và hoàn cảnh. Điều này không có nghĩa là bỏ qua lỗi sai, mà là chọn cách tiếp cận giúp người kia còn khả năng lắng nghe.",
                    "Trong môi trường làm việc, nguyên tắc này đặc biệt hữu ích. Một quản lý biết giảm nhục nhã trong phản hồi thường nhận được nhiều hợp tác hơn một người chỉ thắng bằng quyền lực."),
                Chapter(2, "Quan Tâm Chân Thành", 5,
                    "Một ý tưởng lặp lại trong sách là muốn được yêu mến thì hãy thật sự quan tâm đến người khác. Điều này nghe đơn giản nhưng khó thực hiện, vì phần lớn thời gian ta bước vào cuộc trò chuyện với nhu cầu được nói về mình.",
                    "Quan tâm chân thành thể hiện qua việc nhớ tên, nhớ chi tiết quan trọng, hỏi tiếp thay vì chuyển chủ đề, và lắng nghe mà không chuẩn bị phản bác trong đầu. Những hành động nhỏ ấy gửi tín hiệu rằng người đối diện không chỉ là phương tiện cho mục tiêu của ta.",
                    "Sự chân thành là điểm phân biệt ảnh hưởng tích cực với thao túng. Nếu kỹ thuật giao tiếp không đi cùng sự tôn trọng thật, người khác thường sẽ cảm nhận được."),
                Chapter(3, "Khiến Người Khác Muốn Hợp Tác", 6,
                    "Carnegie không khuyên áp đặt ý kiến. Ông khuyên bắt đầu bằng điều người khác muốn. Khi một đề xuất được nối với lợi ích của người nghe, nó không còn là mệnh lệnh từ bên ngoài mà trở thành lựa chọn có ý nghĩa với họ.",
                    "Một công cụ quan trọng là đặt câu hỏi. Câu hỏi khiến người khác tự đi đến kết luận, thay vì cảm thấy bị ép phải nhận kết luận của ta. Trong đàm phán, giáo dục hay quản lý, cách này thường tạo cam kết bền hơn.",
                    "Điều này cũng đòi hỏi kiên nhẫn. Ảnh hưởng lâu dài không phải là thắng trong một câu nói, mà là tạo điều kiện để người khác thấy mình được tôn trọng trong quá trình thay đổi."),
                Chapter(4, "Giữ Thể Diện", 6,
                    "Ngay cả khi người khác sai, cách ta chỉ ra lỗi sai quyết định chất lượng mối quan hệ sau đó. Nếu làm họ mất mặt, họ có thể sửa hành vi trước mắt nhưng giữ lại sự chống đối bên trong.",
                    "Giữ thể diện là thừa nhận nỗ lực, tách lỗi khỏi giá trị con người, và mở ra đường lui danh dự. Một lời góp ý tốt thường nói rõ điều cần sửa nhưng không đóng đinh người nghe vào sai lầm của họ.",
                    "Đọc lại cuốn sách trong bối cảnh hiện đại, giá trị lớn nhất không nằm ở câu chữ cổ điển mà ở tinh thần: mọi kỹ năng giao tiếp đều bắt đầu từ việc nhìn người khác như một con người đầy đủ.")
            ]),
        Book(
            "Deep Work",
            "deep-work",
            "Cal Newport",
            "Kinh doanh",
            9.10m,
            25,
            "linear-gradient(135deg, #ffecd2 0%, #fcb69f 100%)",
            TargetSvg,
            "Cal Newport bảo vệ năng lực làm việc sâu trong một thế giới đầy thông báo, chat và công việc nông.",
            "Deep Work lập luận rằng khả năng tập trung sâu vào nhiệm vụ khó là lợi thế ngày càng hiếm. Cuốn sách không chỉ phê phán sự xao nhãng, mà còn hướng dẫn cách xây lịch làm việc, môi trường và quy tắc công nghệ để tạo ra giá trị trí tuệ cao.",
            [
                "Làm việc sâu giúp học nhanh và tạo sản phẩm khó sao chép.",
                "Công việc nông tạo cảm giác bận rộn nhưng ít tạo giá trị dài hạn.",
                "Tập trung là kỹ năng cần luyện như cơ bắp.",
                "Lịch làm việc phải bảo vệ những khối thời gian sâu.",
                "Công nghệ nên được dùng có chủ đích thay vì theo mặc định."
            ],
            [
                Chapter(1, "Giá Trị Của Làm Việc Sâu", 6,
                    "Newport định nghĩa làm việc sâu là trạng thái tập trung không phân tâm vào nhiệm vụ đòi hỏi năng lực nhận thức cao. Đây là trạng thái giúp con người học nhanh, giải quyết vấn đề khó và tạo ra sản phẩm có chất lượng.",
                    "Trong nền kinh tế tri thức, nhiều công việc có giá trị nhất không đến từ phản hồi nhanh mà từ suy nghĩ liên tục. Một lập trình viên thiết kế hệ thống, một nhà nghiên cứu viết bài, hay một người sáng tạo xây sản phẩm đều cần thời gian không bị cắt vụn.",
                    "Điểm đáng chú ý là làm việc sâu đang hiếm đi. Chính sự hiếm ấy khiến nó trở thành lợi thế cạnh tranh cho người còn giữ được khả năng tập trung."),
                Chapter(2, "Công Việc Nông và Cảm Giác Bận", 6,
                    "Công việc nông gồm email, họp vụn, chat, cập nhật trạng thái và những nhiệm vụ dễ làm khi bị phân tâm. Chúng không vô dụng, nhưng nếu chiếm toàn bộ ngày làm việc, chúng khiến người ta nhầm bận rộn với hiệu quả.",
                    "Văn hóa phản hồi tức thì làm vấn đề nặng hơn. Khi mọi tin nhắn đều có vẻ khẩn cấp, bộ não không còn đủ khoảng trống để đi sâu vào một bài toán. Người làm việc có thể kết thúc ngày với cảm giác kiệt sức nhưng không tạo được điều quan trọng.",
                    "Newport không bảo loại bỏ hoàn toàn công việc nông. Ông đề xuất giới hạn nó, gom nó vào khung giờ rõ ràng và không để nó quyết định nhịp chính của ngày."),
                Chapter(3, "Thiết Kế Lịch Tập Trung", 7,
                    "Một lời khuyên thực tế là đặt khối làm việc sâu lên lịch như một cuộc hẹn. Nếu chỉ chờ rảnh mới tập trung, lịch sẽ luôn bị những việc nhỏ lấp đầy. Khối sâu cần có giờ bắt đầu, giờ kết thúc và mục tiêu rõ ràng.",
                    "Newport cũng nhấn mạnh nghi thức bắt đầu: chọn nơi làm việc, chuẩn bị tài liệu, tắt nhiễu và xác định tiêu chuẩn hoàn thành. Nghi thức giúp não chuyển trạng thái nhanh hơn và giảm thời gian lưỡng lự.",
                    "Sau mỗi khối sâu, nghỉ ngơi cũng là một phần của hệ thống. Tập trung mạnh không có nghĩa là làm liên tục vô hạn. Bộ não cần hồi phục để lần tập trung sau vẫn có chất lượng."),
                Chapter(4, "Công Nghệ Có Chủ Đích", 6,
                    "Cuốn sách phê phán cách ta dùng công nghệ theo mặc định. Một nền tảng có một chút lợi ích không có nghĩa là nó xứng đáng hiện diện trong đời sống hằng ngày nếu chi phí chú ý quá cao.",
                    "Newport đề xuất đánh giá công cụ theo mục tiêu. Nếu mạng xã hội thật sự phục vụ công việc hoặc quan hệ quan trọng, hãy dùng có luật. Nếu nó chỉ lấp khoảng trống và làm giảm tập trung, cần mạnh dạn loại bỏ hoặc giới hạn.",
                    "Thông điệp cuối cùng là sự tập trung không tự nhiên xuất hiện trong môi trường hiện đại. Nó phải được bảo vệ bằng thiết kế, lịch trình và các ranh giới rõ ràng.")
            ]),
        Book(
            "Nghĩ Giàu Làm Giàu",
            "nghi-giau-lam-giau",
            "Napoleon Hill",
            "Kinh doanh",
            7.50m,
            20,
            "linear-gradient(135deg, #fa709a 0%, #fee140 100%)",
            LayersSvg,
            "Napoleon Hill nhấn mạnh vai trò của mục tiêu rõ ràng, niềm tin và sự kiên trì trong xây dựng thành tựu tài chính.",
            "Nghĩ Giàu Làm Giàu là một tác phẩm self-help kinh điển, mang màu sắc thời đại nhưng vẫn có giá trị ở các ý tưởng về mục tiêu cụ thể, kế hoạch hành động, nhóm hỗ trợ và khả năng bền bỉ sau thất bại.",
            [
                "Mục tiêu càng cụ thể thì hành động càng dễ hình thành.",
                "Niềm tin định hình cách con người phản ứng với cơ hội.",
                "Kế hoạch cần được thử, sửa và làm lại liên tục.",
                "Nhóm cộng sự tốt giúp mở rộng góc nhìn và trách nhiệm.",
                "Kiên trì là lợi thế khi phần lớn người khác bỏ cuộc sớm."
            ],
            [
                Chapter(1, "Mong Muốn Có Hình Dạng", 6,
                    "Hill cho rằng thành tựu bắt đầu từ một mong muốn rõ ràng. Không chỉ là muốn giàu hơn hay thành công hơn, người đọc cần xác định mình muốn gì, vì sao muốn điều đó, sẵn sàng đánh đổi gì và khi nào sẽ hành động.",
                    "Điểm có thể áp dụng trong hiện đại là biến ước muốn thành kế hoạch đo được. Một mục tiêu mơ hồ không tạo áp lực hành động. Một mục tiêu cụ thể giúp người đọc biết hôm nay cần làm gì thay vì chỉ tưởng tượng về kết quả.",
                    "Sự rõ ràng cũng giúp từ chối. Khi biết mục tiêu chính, ta dễ nhận ra những cơ hội trông hấp dẫn nhưng không phục vụ hướng đi dài hạn."),
                Chapter(2, "Niềm Tin và Tự Kỷ Ám Thị", 7,
                    "Một phần sách nói về việc lặp lại mục tiêu để củng cố niềm tin. Nếu đọc theo nghĩa hiện đại, đây có thể xem như quá trình lập trình sự chú ý: điều bạn nhắc lại thường xuyên sẽ dễ xuất hiện hơn trong lựa chọn hằng ngày.",
                    "Niềm tin không thay thế năng lực, nhưng nó ảnh hưởng đến việc bạn có bắt đầu hay không, có dám học khi thất bại hay không. Người tin rằng mình có thể cải thiện sẽ hành động khác người xem thất bại là bằng chứng cố định về bản thân.",
                    "Tuy vậy, niềm tin cần đi cùng phản hồi thực tế. Sự tự tin không kiểm chứng dễ biến thành ảo tưởng. Giá trị bền hơn là niềm tin đủ mạnh để hành động và đủ khiêm tốn để sửa sai."),
                Chapter(3, "Kế Hoạch và Nhóm Hỗ Trợ", 7,
                    "Hill nhấn mạnh vai trò của kế hoạch có tổ chức. Kế hoạch đầu tiên thường sai, nhưng người thành công không xem sai lầm là dấu chấm hết. Họ xem đó là thông tin để chỉnh chiến lược.",
                    "Khái niệm nhóm mastermind có thể hiểu là mạng lưới người giúp bạn nghĩ rõ hơn: cố vấn, cộng sự, bạn cùng ngành hoặc nhóm học tập. Một người đơn độc dễ mắc điểm mù; nhóm tốt giúp tăng tiêu chuẩn và tạo trách nhiệm.",
                    "Bài học thực tế là đừng chỉ hỏi mình có động lực không. Hãy hỏi hệ thống quanh mình có giúp duy trì hành động không: ai phản hồi, ai nhắc tiến độ, ai bổ sung năng lực mình thiếu.")
            ]),
        Book(
            "Ikigai",
            "ikigai",
            "Héctor García",
            "Triết học",
            8.50m,
            18,
            "linear-gradient(135deg, #a18cd1 0%, #fbc2eb 100%)",
            SmileSvg,
            "Ikigai gợi ý một cách sống có mục đích, chậm rãi và bền vững từ quan sát các cộng đồng sống thọ.",
            "Ikigai kết hợp triết lý sống Nhật Bản với những quan sát về tuổi thọ, cộng đồng và trạng thái flow. Cuốn sách không ép người đọc tìm một sứ mệnh lớn lao, mà gợi ý rằng lý do sống thường nằm trong những hoạt động nhỏ, đều đặn và có ý nghĩa.",
            [
                "Mục đích sống không nhất thiết phải lớn lao hay gây ấn tượng.",
                "Nhịp sống đều, vận động nhẹ và cộng đồng gần gũi hỗ trợ tuổi thọ.",
                "Flow xuất hiện khi thử thách và năng lực cân bằng.",
                "Ăn vừa đủ và duy trì kết nối xã hội là nền tảng sức khỏe.",
                "Ikigai được nuôi dưỡng bằng hành động nhỏ có ý nghĩa."
            ],
            [
                Chapter(1, "Lý Do Để Thức Dậy", 4,
                    "Ikigai có thể hiểu là lý do khiến bạn muốn bắt đầu ngày mới. Nó không nhất thiết phải là một mục tiêu vĩ đại. Với một số người, đó là chăm cây, dạy học, làm nghề thủ công, nấu ăn cho gia đình hoặc phục vụ cộng đồng nhỏ.",
                    "Điểm đẹp của khái niệm này là nó kéo mục đích sống về gần đời thường. Người đọc không cần chờ một khoảnh khắc khai sáng. Họ có thể bắt đầu bằng việc quan sát hoạt động nào khiến mình thấy có ích và sống động.",
                    "Khi mục đích được đặt trong hành vi hằng ngày, nó ít bị phụ thuộc vào thành công bên ngoài. Một ngày bình thường vẫn có thể có ý nghĩa."),
                Chapter(2, "Flow và Công Việc Có Ý Nghĩa", 5,
                    "Flow xuất hiện khi bạn làm một việc vừa đủ khó để phải tập trung, nhưng không quá khó đến mức tuyệt vọng. Trong trạng thái này, thời gian trôi nhanh và cái tôi bớt ồn ào.",
                    "Ikigai gợi ý rằng đời sống tốt cần những hoạt động đưa ta vào flow thường xuyên. Đó có thể là viết, sửa đồ, chơi nhạc, lập trình, chăm sóc người khác hoặc học một kỹ năng mới.",
                    "Điều quan trọng là giảm nhiễu. Một hoạt động có ý nghĩa vẫn khó tạo flow nếu liên tục bị thông báo và so sánh xã hội kéo ra ngoài."),
                Chapter(3, "Nhịp Sống Của Người Sống Thọ", 5,
                    "Sách quan sát các cộng đồng sống thọ, nơi con người vận động nhẹ mỗi ngày, ăn uống vừa phải và duy trì quan hệ xã hội gần. Sức khỏe ở đây không phải dự án cực đoan, mà là kết quả của nhịp sống được thiết kế tốt.",
                    "Vận động không nhất thiết là phòng tập cường độ cao. Đi bộ, làm vườn, làm việc nhà và tham gia hoạt động cộng đồng tạo ra sự chuyển động tự nhiên. Những chuyển động nhỏ này bền hơn các chiến dịch ngắn hạn.",
                    "Bài học là sức khỏe không tách khỏi môi trường. Nếu đời sống hằng ngày buộc cơ thể ngồi yên, ăn vội và cô lập, ý chí cá nhân sẽ phải gồng quá nhiều."),
                Chapter(4, "Sống Chậm Nhưng Không Trì Trệ", 4,
                    "Sống chậm không có nghĩa là thiếu tham vọng. Nó có nghĩa là chọn nhịp sống đủ bền để không đánh đổi sức khỏe tinh thần lấy thành tích ngắn hạn.",
                    "Khi biết điều gì thật sự quan trọng, người đọc dễ bỏ bớt hoạt động chỉ nhằm gây ấn tượng. Ikigai vì thế không phải lời kêu gọi rút khỏi đời sống, mà là lời mời sống có chọn lọc hơn.",
                    "Một đời sống có mục đích thường không ồn ào. Nó được xây bằng những việc nhỏ được làm với sự hiện diện và đều đặn.")
            ]),
        Book(
            "Homo Deus",
            "homo-deus",
            "Yuval Noah Harari",
            "Khoa học",
            8.90m,
            30,
            "linear-gradient(135deg, #a1c4fd 0%, #c2e9fb 100%)",
            RocketSvg,
            "Harari đặt câu hỏi con người sẽ theo đuổi điều gì khi đã kiểm soát tốt hơn đói nghèo, dịch bệnh và chiến tranh.",
            "Homo Deus nhìn về tương lai của nhân loại trong bối cảnh công nghệ sinh học, dữ liệu lớn và AI phát triển. Sách đặt ra câu hỏi: khi con người có khả năng nâng cấp cơ thể, kéo dài tuổi thọ và dự đoán hành vi, những giá trị nhân văn quen thuộc sẽ thay đổi ra sao.",
            [
                "Mục tiêu tương lai của loài người có thể chuyển từ sinh tồn sang nâng cấp.",
                "Chủ nghĩa nhân văn đặt trải nghiệm cá nhân làm trung tâm ý nghĩa.",
                "Dữ liệu và thuật toán thách thức niềm tin rằng cá nhân hiểu mình nhất.",
                "Công nghệ nâng cấp có thể tạo bất bình đẳng sinh học mới.",
                "Câu hỏi quan trọng là ai kiểm soát dữ liệu và phục vụ mục tiêu nào."
            ],
            [
                Chapter(1, "Từ Sinh Tồn Đến Nâng Cấp", 6,
                    "Harari cho rằng nhiều vấn đề từng thống trị lịch sử như nạn đói, dịch bệnh và chiến tranh đã được kiểm soát tốt hơn trước, dù chưa biến mất. Khi áp lực sinh tồn giảm xuống, tham vọng mới xuất hiện.",
                    "Con người có thể không chỉ muốn sống, mà muốn sống lâu hơn, hạnh phúc hơn và mạnh hơn. Công nghệ sinh học, dược phẩm, thuật toán và thiết bị số mở ra khả năng can thiệp vào cơ thể lẫn tâm trí.",
                    "Điểm đáng suy nghĩ là mỗi bước nâng cấp đều kéo theo câu hỏi đạo đức: ai được tiếp cận, ai bị bỏ lại, và tiêu chuẩn nào quyết định một con người tốt hơn."),
                Chapter(2, "Tôn Giáo Của Con Người", 6,
                    "Harari mô tả chủ nghĩa nhân văn như hệ niềm tin đặt trải nghiệm con người ở trung tâm. Trong nghệ thuật, chính trị và tiêu dùng, câu hỏi thường là: bạn cảm thấy gì, bạn chọn gì, điều gì đúng với bạn.",
                    "Cách nhìn này từng giải phóng con người khỏi nhiều quyền lực bên ngoài. Nhưng nó cũng dựa trên giả định rằng cá nhân hiểu bản thân và cảm xúc cá nhân là nguồn chỉ dẫn đáng tin.",
                    "Khi khoa học thần kinh và dữ liệu hành vi phát triển, giả định này bắt đầu bị thách thức. Nếu thuật toán hiểu ta tốt hơn ta hiểu mình, quyền lựa chọn sẽ thay đổi thế nào?"),
                Chapter(3, "Dữ Liệu và Thuật Toán", 6,
                    "Trong đời sống hiện đại, mỗi hành vi tạo ra dữ liệu: tìm kiếm, mua sắm, di chuyển, nhịp tim, giấc ngủ, tương tác xã hội. Dữ liệu này cho phép hệ thống dự đoán sở thích và quyết định với độ chính xác ngày càng cao.",
                    "Điều tiện lợi là các thuật toán có thể giúp chẩn đoán bệnh, gợi ý học tập, tối ưu giao thông và cá nhân hóa dịch vụ. Điều nguy hiểm là chúng cũng có thể thao túng lựa chọn, củng cố định kiến và tập trung quyền lực.",
                    "Harari buộc người đọc nhìn dữ liệu như một vấn đề chính trị, không chỉ kỹ thuật. Ai sở hữu dữ liệu có thể sở hữu năng lực hiểu và định hình hành vi."),
                Chapter(4, "Bất Bình Đẳng Sinh Học", 6,
                    "Nếu công nghệ chỉ cải thiện tiện ích thì bất bình đẳng đã đáng lo; nếu nó cải thiện trí nhớ, tuổi thọ, cảm xúc và năng lực sinh học, khoảng cách xã hội có thể sâu hơn nhiều.",
                    "Một nhóm người có thể mua giáo dục tốt hơn là chuyện cũ. Nhưng nếu họ mua được cơ thể bền hơn, não bộ tối ưu hơn hoặc thuật toán chăm sóc cá nhân tốt hơn, xã hội sẽ đối mặt dạng phân tầng mới.",
                    "Cuốn sách không nói điều này chắc chắn xảy ra, nhưng dùng nó như cảnh báo: công nghệ không tự động công bằng. Công bằng phải được thiết kế bằng chính sách và giá trị."),
                Chapter(5, "Con Người Trong Tương Lai", 6,
                    "Homo Deus kết thúc bằng những câu hỏi hơn là câu trả lời. Nếu con người không còn là trung tâm duy nhất của trí tuệ, ta sẽ định nghĩa phẩm giá, tự do và trách nhiệm như thế nào?",
                    "Harari khuyến khích người đọc không chỉ hỏi công nghệ có thể làm gì, mà hỏi nó nên phục vụ đời sống nào. Không phải mọi thứ có thể tối ưu đều đáng tối ưu.",
                    "Giá trị của cuốn sách nằm ở việc mở rộng trí tưởng tượng đạo đức. Nó khiến người đọc thấy tương lai không chỉ là sản phẩm của kỹ sư, mà còn là lựa chọn của xã hội.")
            ]),
        Book(
            "The Psychology of Money",
            "the-psychology-of-money",
            "Morgan Housel",
            "Kinh doanh",
            9.20m,
            23,
            "linear-gradient(135deg, #fddb92 0%, #d1fdff 100%)",
            DollarSvg,
            "Morgan Housel giải thích vì sao hành vi và cảm xúc thường quan trọng hơn công thức tài chính.",
            "The Psychology of Money xem tiền bạc như một vấn đề hành vi. Housel cho rằng mỗi người ra quyết định từ trải nghiệm riêng, nên điều hợp lý với người này có thể vô lý với người khác. Giàu bền vững cần sự khiêm tốn, biên an toàn và khả năng ở lại cuộc chơi đủ lâu.",
            [
                "Tiền bạc chịu ảnh hưởng mạnh từ trải nghiệm cá nhân.",
                "Giàu có và trông giàu có là hai điều khác nhau.",
                "Thời gian là thành phần quan trọng nhất của lãi kép.",
                "Biên an toàn giúp sống sót qua bất định.",
                "Mục tiêu sâu nhất của tiền là quyền kiểm soát thời gian."
            ],
            [
                Chapter(1, "Mỗi Người Có Một Lịch Sử Tiền Bạc", 5,
                    "Một người trưởng thành trong lạm phát, khủng hoảng hoặc nghèo khó sẽ nhìn rủi ro khác người lớn lên trong ổn định. Housel nhấn mạnh rằng quyết định tài chính không chỉ đến từ bảng tính, mà từ ký ức và cảm xúc.",
                    "Điều này giải thích vì sao hai người thông minh có thể bất đồng gay gắt về đầu tư, tiết kiệm hay nợ. Họ không chỉ phân tích dữ liệu khác nhau; họ đang mang theo những đời sống khác nhau.",
                    "Bài học là hãy khiêm tốn khi đánh giá lựa chọn tiền bạc của người khác. Một quyết định trông vô lý có thể hợp lý trong bối cảnh trải nghiệm của họ."),
                Chapter(2, "Giàu Có Không Phải Là Trông Giàu", 6,
                    "Housel phân biệt giữa tài sản thật và tín hiệu tiêu dùng. Một chiếc xe đắt tiền cho thấy tiền đã được tiêu, không chứng minh người đó còn nhiều tài sản. Của cải thật thường vô hình: khoản tiết kiệm, quyền lựa chọn và sự tự do khỏi áp lực tài chính.",
                    "Xã hội dễ ngưỡng mộ thứ nhìn thấy được, nên nhiều người đánh đổi tự do thật để mua biểu tượng của sự giàu. Cuốn sách khuyên người đọc hỏi mình muốn được ngưỡng mộ hay muốn được tự do.",
                    "Đây là một trong những chương thực tế nhất, vì nó chuyển mục tiêu tài chính từ hình ảnh sang năng lực sống. Tiền hữu ích nhất khi nó cho bạn khả năng nói không."),
                Chapter(3, "Lãi Kép và Thời Gian", 6,
                    "Lãi kép không chỉ cần tỷ suất, mà cần thời gian. Thành công của nhiều nhà đầu tư lớn đến từ việc ở lại cuộc chơi rất lâu, tránh những sai lầm phá hủy và để tăng trưởng tích lũy.",
                    "Điểm khó là thời gian dài đòi hỏi tâm lý vững. Thị trường biến động, người khác khoe lợi nhuận nhanh, và cảm xúc muốn hành động liên tục. Người có lợi thế không phải người luôn dự đoán đúng, mà là người không tự loại mình khỏi cuộc chơi.",
                    "Bài học áp dụng rộng hơn tài chính: nhiều kết quả tốt trong đời sống cần sự bền bỉ nhàm chán hơn là nước đi thiên tài."),
                Chapter(4, "Biên An Toàn", 6,
                    "Không kế hoạch nào dự đoán hết tương lai. Vì vậy biên an toàn không phải sự bi quan, mà là sự tôn trọng bất định. Tiền dự phòng, nợ thấp và kỳ vọng vừa phải giúp con người không phải bán tháo hay ra quyết định trong hoảng loạn.",
                    "Housel cũng nhấn mạnh rằng tối ưu hóa quá mức dễ làm hệ thống mong manh. Một kế hoạch tài chính trông hiệu quả trên giấy có thể sụp đổ nếu nó không có chỗ cho bệnh tật, mất việc hoặc sai lầm.",
                    "Sự giàu bền vững thường đến từ việc tránh thảm họa hơn là liên tục tìm cơ hội lớn. Còn ở lại cuộc chơi thì lãi kép mới có thời gian hoạt động."),
                Chapter(5, "Tiền Là Quyền Kiểm Soát Thời Gian", 5,
                    "Kết luận quan trọng của Housel là mục tiêu tối hậu của tiền không phải là mua thêm đồ, mà là kiểm soát thời gian và lựa chọn. Có tiền dự phòng nghĩa là có thể nghỉ, chuyển việc, chăm người thân hoặc từ chối cơ hội không phù hợp.",
                    "Cách nhìn này làm tài chính cá nhân bớt khoe khoang và gần với chất lượng sống hơn. Người đọc được khuyến khích thiết kế tiền bạc quanh đời sống mình muốn, không quanh bảng xếp hạng của người khác.",
                    "Một kế hoạch tài chính tốt vì vậy không chỉ hỏi lợi suất bao nhiêu, mà hỏi nó có giúp bạn ngủ ngon và sống đúng giá trị hơn không.")
            ]),
        Book("Người Giàu Có Nhất Thành Babylon", "nguoi-giau-co-nhat-thanh-babylon", "George S. Clason", "Kinh doanh", 8.20m, 17, "linear-gradient(135deg, #d4fc79 0%, #96e6a1 100%)", DollarSvg, "Các câu chuyện ngụ ngôn ở Babylon diễn giải những nguyên tắc tài chính cá nhân căn bản.", "Cuốn sách dùng lối kể chuyện cổ điển để nói về tiết kiệm, kiểm soát chi tiêu, đầu tư và bảo vệ vốn. Giá trị của nó nằm ở sự đơn giản: xây tài sản không bắt đầu bằng bí quyết phức tạp mà bằng kỷ luật lặp lại.", MoneyTakeaways, MoneyChapters),
        Book("Outliers", "outliers", "Malcolm Gladwell", "Tâm lý học", 8.60m, 22, "linear-gradient(135deg, #ff9a9e 0%, #fecfef 100%)", ChartSvg, "Gladwell cho thấy thành công không chỉ đến từ tài năng mà còn từ thời điểm, môi trường và cơ hội tích lũy.", "Outliers phân tích những người thành công vượt trội để chỉ ra rằng tài năng không tồn tại trong chân không. Gia đình, văn hóa, thời điểm sinh, cơ hội luyện tập và hệ thống xã hội đều góp phần tạo nên kết quả.", OutliersTakeaways, OutliersChapters),
        Book("Bước Chậm Lại Giữa Thế Gian Vội Vã", "buoc-cham-lai-giua-the-gian-voi-va", "Hae Min", "Triết học", 8.30m, 16, "linear-gradient(135deg, #89f7fe 0%, #66a6ff 100%)", LeafSvg, "Hae Min đưa ra những suy ngẫm ngắn về bình an, quan hệ và cách sống chậm.", "Cuốn sách là tập hợp các suy ngẫm nhẹ nhàng về việc dừng lại, quan sát tâm trí và tử tế với bản thân. Nó phù hợp với người đọc cần một khoảng lặng trong đời sống nhiều áp lực.", SlowTakeaways, SlowChapters),
        Book("Start With Why", "start-with-why", "Simon Sinek", "Kinh doanh", 8.80m, 19, "linear-gradient(135deg, #f6d365 0%, #fda085 100%)", TargetSvg, "Sinek giải thích vì sao tổ chức truyền cảm hứng thường bắt đầu từ lý do tồn tại trước khi nói đến sản phẩm.", "Start With Why xoay quanh ý tưởng rằng con người không chỉ mua thứ bạn làm, họ bị thu hút bởi lý do bạn làm điều đó. Cuốn sách hữu ích cho xây dựng thương hiệu, lãnh đạo và truyền thông sản phẩm.", WhyTakeaways, WhyChapters),
        Book("The Lean Startup", "the-lean-startup", "Eric Ries", "Kinh doanh", 8.70m, 21, "linear-gradient(135deg, #30cfd0 0%, #330867 100%)", RocketSvg, "Eric Ries trình bày cách xây sản phẩm bằng thử nghiệm nhỏ, học nhanh và giảm lãng phí.", "The Lean Startup giới thiệu vòng lặp build-measure-learn, MVP và validated learning. Thay vì dành quá lâu để xây thứ có thể không ai cần, nhóm sản phẩm nên kiểm chứng giả định quan trọng nhất càng sớm càng tốt.", LeanTakeaways, LeanChapters),
        Book("Good to Great", "good-to-great", "Jim Collins", "Kinh doanh", 8.65m, 24, "linear-gradient(135deg, #434343 0%, #000000 100%)", ChartSvg, "Jim Collins nghiên cứu vì sao một số công ty chuyển từ tốt sang vĩ đại còn phần lớn thì không.", "Good to Great tổng hợp các mẫu hành vi của tổ chức có bước nhảy vọt bền vững: lãnh đạo khiêm tốn, kỷ luật, chọn đúng người và tập trung vào giao điểm năng lực, kinh tế và đam mê.", GreatTakeaways, GreatChapters),
        Book("The 7 Habits of Highly Effective People", "the-7-habits", "Stephen R. Covey", "Tự lực", 8.95m, 26, "linear-gradient(135deg, #667db6 0%, #0082c8 50%, #0082c8 100%)", LayersSvg, "Covey trình bày bảy thói quen nền tảng để sống chủ động, có nguyên tắc và hợp tác hiệu quả.", "7 Habits không chỉ nói về năng suất cá nhân mà còn về trưởng thành bên trong: từ phụ thuộc, đến độc lập, rồi đến phụ thuộc lẫn nhau một cách lành mạnh. Sách đặt nguyên tắc và tính cách cao hơn mẹo quản lý thời gian.", HabitsTakeaways, HabitsChapters),
        Book("Mindset", "mindset", "Carol S. Dweck", "Tâm lý học", 8.85m, 18, "linear-gradient(135deg, #c471f5 0%, #fa71cd 100%)", LightbulbSvg, "Dweck phân biệt tư duy cố định và tư duy phát triển trong học tập, công việc và quan hệ.", "Mindset giải thích rằng niềm tin về khả năng học hỏi ảnh hưởng mạnh đến cách ta đối diện thử thách. Người có tư duy phát triển không xem thất bại là bản án về năng lực, mà là phản hồi để cải thiện.", MindsetTakeaways, MindsetChapters),
        Book("The Power of Now", "the-power-of-now", "Eckhart Tolle", "Triết học", 8.40m, 17, "linear-gradient(135deg, #84fab0 0%, #8fd3f4 100%)", LeafSvg, "Tolle mời người đọc quay về hiện tại để bớt bị kéo đi bởi lo âu và câu chuyện trong đầu.", "The Power of Now là một cuốn sách tinh thần về sự hiện diện. Dù giọng văn có màu sắc chiêm nghiệm, ý chính có thể hiểu thực tế: rất nhiều khổ sở đến từ việc đồng nhất bản thân với dòng suy nghĩ không ngừng.", NowTakeaways, NowChapters),
        Book("Essentialism", "essentialism", "Greg McKeown", "Tự lực", 8.75m, 18, "linear-gradient(135deg, #ff758c 0%, #ff7eb3 100%)", TargetSvg, "Essentialism hướng người đọc đến việc chọn ít hơn nhưng quan trọng hơn.", "Essentialism không cổ vũ làm nhiều hơn, mà cổ vũ phân biệt điều thật sự thiết yếu với tiếng ồn. Cuốn sách phù hợp với người luôn bận nhưng cảm thấy tiến độ quan trọng không nhúc nhích.", EssentialTakeaways, EssentialChapters),
        Book("Range", "range", "David Epstein", "Khoa học", 8.55m, 20, "linear-gradient(135deg, #43cea2 0%, #185a9d 100%)", GlobeSvg, "Epstein cho thấy trong nhiều lĩnh vực phức tạp, kinh nghiệm rộng có thể quan trọng không kém chuyên môn sớm.", "Range phản biện quan điểm phải chuyên môn hóa cực sớm trong mọi lĩnh vực. Cuốn sách phân biệt môi trường học tập tử tế, nơi phản hồi rõ ràng, với môi trường phức tạp, nơi sự linh hoạt và liên tưởng rộng tạo lợi thế.", RangeTakeaways, RangeChapters),
        Book("Dare to Lead", "dare-to-lead", "Brené Brown", "Kinh doanh", 8.45m, 19, "linear-gradient(135deg, #f7971e 0%, #ffd200 100%)", UsersSvg, "Brené Brown kết nối lãnh đạo với sự can đảm, tính dễ tổn thương và niềm tin.", "Dare to Lead nhìn lãnh đạo không chỉ là ra quyết định cứng rắn, mà là khả năng tạo môi trường tin cậy để con người nói thật, học từ sai lầm và cùng chịu trách nhiệm.", LeadTakeaways, LeadChapters),
        Book("The Design of Everyday Things", "the-design-of-everyday-things", "Don Norman", "Khoa học", 8.60m, 21, "linear-gradient(135deg, #74ebd5 0%, #acb6e5 100%)", LayersSvg, "Don Norman giải thích vì sao thiết kế tốt khiến hành động trở nên dễ hiểu, còn thiết kế tệ khiến người dùng tự trách mình.", "The Design of Everyday Things là nền tảng cho tư duy thiết kế lấy người dùng làm trung tâm. Sách cho thấy lỗi người dùng nhiều khi là lỗi thiết kế: dấu hiệu không rõ, phản hồi thiếu, ánh xạ sai và ràng buộc kém.", DesignTakeaways, DesignChapters)
    ];

    private static SeedBook Book(
        string title,
        string slug,
        string author,
        string category,
        decimal rating,
        int readingTimeMinutes,
        string coverGradient,
        string coverSvg,
        string description,
        string introduction,
        List<string> takeaways,
        List<SeedChapter> chapters)
    {
        return new SeedBook(title, slug, author, category, rating, readingTimeMinutes, coverGradient, coverSvg, $"/images/covers/{slug}.svg", description, introduction, takeaways, chapters);
    }

    private static SeedChapter Chapter(int number, string title, int readingTimeMinutes, params string[] paragraphs)
    {
        var contentHtml = string.Join(Environment.NewLine, paragraphs.Select(paragraph => $"<p>{WebUtility.HtmlEncode(paragraph)}</p>"));
        return new SeedChapter(number, title, readingTimeMinutes, contentHtml);
    }

    private static readonly List<string> MoneyTakeaways =
    [
        "Tiết kiệm trước khi chi tiêu biến xây tài sản thành thói quen.",
        "Chi tiêu không kiểm soát sẽ tăng theo thu nhập nếu không có luật rõ.",
        "Tiền cần được đưa vào tài sản có khả năng sinh thêm tiền.",
        "Bảo vệ vốn quan trọng không kém tìm lợi nhuận.",
        "Tài chính cá nhân bền vững đến từ kỷ luật lặp lại."
    ];

    private static readonly List<SeedChapter> MoneyChapters =
    [
        Chapter(1, "Trả Cho Mình Trước", 5, "Nguyên tắc nổi tiếng nhất của sách là giữ lại một phần thu nhập trước khi chi tiêu. Nếu chờ đến cuối tháng mới tiết kiệm, phần còn lại thường quá ít hoặc không còn gì.", "Việc trả cho mình trước biến tiết kiệm thành nghĩa vụ với tương lai. Nó cũng thay đổi tâm lý: bạn không còn xem tiết kiệm là phần dư, mà là khoản đầu tư đầu tiên của mỗi kỳ thu nhập.", "Khi thói quen này lặp lại đủ lâu, số tiền nhỏ trở thành nền móng cho những lựa chọn lớn hơn như đầu tư, học kỹ năng hoặc vượt qua biến cố."),
        Chapter(2, "Kiểm Soát Ham Muốn", 4, "Clason cho rằng ham muốn sẽ luôn mở rộng để tiêu hết thu nhập nếu không được giới hạn. Kiếm nhiều hơn không tự động làm một người giàu hơn nếu mức sống tăng cùng tốc độ.", "Ngân sách trong sách không mang nghĩa bóp nghẹt đời sống, mà là công cụ phân biệt điều cần thiết với điều chỉ làm ta thỏa mãn nhất thời.", "Một kế hoạch chi tiêu tốt giúp tiền đi theo thứ tự ưu tiên. Nó cho phép bạn tận hưởng hiện tại mà không đánh cắp hoàn toàn tương lai."),
        Chapter(3, "Đầu Tư Thận Trọng", 4, "Tiền tiết kiệm chỉ là bước đầu. Muốn tài sản lớn lên, tiền cần được đặt vào nơi có khả năng tạo thêm tiền. Nhưng sách cảnh báo mạnh mẽ về việc chạy theo lời hứa lợi nhuận nhanh.", "Người khôn ngoan không đầu tư vào thứ mình không hiểu hoặc giao tiền cho người không có năng lực trong lĩnh vực đó. Sự thận trọng này nghe nhàm chán nhưng giúp tránh những sai lầm phá hủy.", "Tư duy quan trọng là bảo vệ vốn trước, tăng trưởng sau. Một khoản lỗ lớn có thể xóa sạch nhiều năm kỷ luật."),
        Chapter(4, "Làm Chủ Tài Chính Cá Nhân", 4, "Cuốn sách không trình bày tài chính như bí mật của người thông minh đặc biệt. Nó xem tài chính như một bộ thói quen: tiết kiệm, kiểm soát, đầu tư và học hỏi.", "Chính sự đơn giản làm sách vẫn còn hữu ích. Người đọc không cần bắt đầu bằng sản phẩm phức tạp; họ cần bắt đầu bằng việc biết tiền của mình đang đi đâu.", "Khi các nguyên tắc cơ bản được duy trì, tài chính cá nhân bớt phụ thuộc vào may mắn. Nó trở thành hệ thống nhỏ nhưng bền.")
    ];

    private static readonly List<string> OutliersTakeaways =
    [
        "Thành công là kết quả của tài năng cộng với cơ hội tích lũy.",
        "Lợi thế nhỏ ban đầu có thể được hệ thống khuếch đại.",
        "Luyện tập sâu cần môi trường và phản hồi phù hợp.",
        "Văn hóa ảnh hưởng đến cách con người giao tiếp, học tập và làm việc.",
        "Muốn công bằng hơn cần thiết kế cơ hội tốt hơn, không chỉ khen người thắng."
    ];

    private static readonly List<SeedChapter> OutliersChapters =
    [
        Chapter(1, "Không Chỉ Là Thiên Tài", 5, "Gladwell phản đối câu chuyện thành công chỉ dựa vào năng lực cá nhân. Những người xuất sắc thường có tài năng thật, nhưng tài năng ấy được đặt vào đúng thời điểm, đúng môi trường và đúng chuỗi cơ hội.", "Cách nhìn này không phủ nhận nỗ lực. Nó chỉ nhắc rằng nỗ lực cần điều kiện để chuyển thành kết quả. Một người không có cơ hội luyện tập, được hướng dẫn hoặc tiếp cận công cụ tốt sẽ khó biến tiềm năng thành thành tựu.", "Chương này giúp người đọc khiêm tốn hơn khi nhìn thành công của người khác và nhân ái hơn khi nhìn thất bại của người chưa có điều kiện."),
        Chapter(2, "Cơ Hội Tích Lũy", 5, "Một lợi thế nhỏ ban đầu có thể tạo ra khoảng cách lớn về sau. Ví dụ một đứa trẻ lớn hơn vài tháng trong cùng lứa tuổi thể thao có thể được chọn vào đội tốt, được huấn luyện nhiều hơn và dần thật sự giỏi hơn.", "Hệ thống thường gọi kết quả cuối là tài năng mà quên mất quá trình tích lũy cơ hội phía trước. Khi đã vào nhóm tốt hơn, người đó nhận thêm tài nguyên, kỳ vọng và sự tự tin.", "Bài học không phải là phủ nhận người giỏi, mà là nhìn kỹ cách môi trường tạo ra người giỏi. Thiết kế hệ thống công bằng hơn nghĩa là mở rộng cơ hội sớm cho nhiều người hơn."),
        Chapter(3, "Luyện Tập và Chuyên Môn", 6, "Outliers phổ biến ý tưởng luyện tập rất nhiều để đạt chuyên môn. Dù con số cụ thể không nên hiểu máy móc, thông điệp chính vẫn mạnh: năng lực lớn cần thời gian, phản hồi và sự tập trung bền bỉ.", "Không phải giờ luyện tập nào cũng như nhau. Luyện tập có giá trị cần khó vừa đủ, có người phản hồi, có tiêu chuẩn rõ và có cơ hội sửa sai. Lặp lại trong vô thức không tạo ra tiến bộ như luyện tập có chủ đích.", "Chương này cũng cho thấy cơ hội và nỗ lực gắn với nhau. Người có điều kiện luyện tập nhiều hơn sẽ có cơ hội biến nỗ lực thành chuyên môn hơn."),
        Chapter(4, "Văn Hóa và Bối Cảnh", 6, "Gladwell mở rộng phân tích sang văn hóa: cách con người nhìn quyền lực, hợp tác, rủi ro và giao tiếp có thể ảnh hưởng đến kết quả trong trường học, doanh nghiệp hay buồng lái máy bay.", "Một thông điệp quan trọng là hành vi cá nhân thường mang theo di sản tập thể. Nếu muốn cải thiện hiệu quả hoặc an toàn, đôi khi ta phải thay đổi quy tắc giao tiếp và môi trường, không chỉ đào tạo từng cá nhân.", "Thành công vì thế là câu chuyện của cả người và bối cảnh. Hiểu bối cảnh giúp ta thiết kế tổ chức tốt hơn và bớt thần thoại hóa cá nhân chiến thắng.")
    ];

    private static readonly List<string> SlowTakeaways =
    [
        "Dừng lại giúp ta nhìn cảm xúc như một hiện tượng, không phải toàn bộ sự thật.",
        "Tử tế với bản thân tạo nền tảng để tử tế với người khác.",
        "Nhiều mối quan hệ cần được hiểu hơn là cần thắng.",
        "Buông bớt kiểm soát có thể tạo không gian cho bình an.",
        "Sống chậm là chọn nhịp bền, không phải trốn khỏi trách nhiệm."
    ];

    private static readonly List<SeedChapter> SlowChapters =
    [
        Chapter(1, "Dừng Lại Để Nhìn Rõ", 4, "Khi đời sống quá nhanh, ta dễ phản ứng trước khi kịp hiểu mình đang cảm thấy gì. Hae Min khuyên người đọc tạo một khoảng dừng nhỏ giữa kích thích và phản ứng.", "Khoảng dừng ấy không giải quyết mọi vấn đề, nhưng nó trả lại quyền lựa chọn. Khi nhận ra mình đang giận, đang sợ hoặc đang xấu hổ, ta không còn hoàn toàn bị cảm xúc kéo đi.", "Đây là một kiểu thực hành giản dị: thở chậm hơn, đi bộ không vội, viết xuống điều đang làm mình nặng lòng."),
        Chapter(2, "Tử Tế Với Chính Mình", 4, "Nhiều người nói với bản thân bằng giọng khắc nghiệt hơn rất nhiều so với cách họ nói với bạn bè. Cuốn sách nhắc rằng sự nghiêm khắc quá mức không làm ta tốt hơn, nó chỉ khiến ta kiệt sức.", "Tử tế với bản thân không có nghĩa là nuông chiều mọi yếu điểm. Nó có nghĩa là nhìn lỗi lầm như thông tin để học, không phải bản án về giá trị con người.", "Khi bớt tự kết án, ta có thêm năng lượng để sửa đổi. Sự bình an không đối lập với trưởng thành; nó là đất tốt cho trưởng thành."),
        Chapter(3, "Quan Hệ Không Phải Cuộc Thi", 4, "Trong nhiều cuộc cãi vã, nhu cầu được hiểu quan trọng hơn nhu cầu chứng minh mình đúng. Nếu cả hai chỉ cố thắng, mối quan hệ có thể thua dù lập luận của một người đúng.", "Hae Min khuyến khích lắng nghe chậm, nói rõ cảm xúc và để người khác có không gian giữ thể diện. Tình thương đôi khi nằm ở việc không đẩy câu chuyện đến tận cùng thắng thua.", "Điều này đặc biệt khó trong thời đại phản ứng nhanh. Nhưng những mối quan hệ quan trọng thường cần nhịp chậm hơn nhịp của mạng xã hội."),
        Chapter(4, "Sống Chậm Trong Thế Giới Vội", 4, "Sống chậm không phải là bỏ hết mục tiêu. Nó là đặt mục tiêu vào một nhịp mà cơ thể và tâm trí có thể chịu được lâu dài.", "Cuốn sách mời người đọc bớt nhồi nhét mọi khoảng trống bằng việc phải làm. Một khoảng rỗng có thể là nơi ta nghe lại chính mình.", "Khi biết mình thật sự coi trọng điều gì, ta dễ từ chối những thứ chỉ làm đời sống ồn hơn.")
    ];

    private static readonly List<string> WhyTakeaways = ["Người truyền cảm hứng bắt đầu bằng lý do, không chỉ bằng sản phẩm.", "Why tạo niềm tin và sự nhất quán cho thương hiệu.", "How là cách tổ chức biến niềm tin thành hành động.", "What là biểu hiện cụ thể nhưng không phải cốt lõi sâu nhất.", "Khách hàng trung thành khi họ thấy giá trị của mình được phản chiếu."];
    private static readonly List<SeedChapter> WhyChapters = [
        Chapter(1, "Vòng Tròn Vàng", 5, "Sinek mô tả ba lớp giao tiếp: Why, How và What. Phần lớn tổ chức bắt đầu từ việc mình bán gì. Những tổ chức truyền cảm hứng bắt đầu từ lý do mình tồn tại.", "Why không phải khẩu hiệu đẹp. Nó là niềm tin định hướng quyết định sản phẩm, văn hóa và cách giao tiếp.", "Khi Why rõ, người nghe dễ hiểu vì sao nên quan tâm chứ không chỉ biết sản phẩm có tính năng gì."),
        Chapter(2, "Niềm Tin Trước Tính Năng", 5, "Con người không ra quyết định chỉ bằng lý trí. Họ bị thu hút bởi cảm giác tin tưởng và sự đồng điệu giá trị.", "Một thương hiệu nói rõ niềm tin của mình sẽ thu hút những người có cùng niềm tin. Điều này tạo lòng trung thành mạnh hơn giảm giá hoặc danh sách tính năng.", "Tính năng vẫn quan trọng, nhưng nó nên chứng minh cho Why thay vì thay thế Why."),
        Chapter(3, "Lãnh Đạo Bằng Lý Do", 5, "Trong nội bộ, Why giúp đội ngũ ra quyết định khi không có người lãnh đạo đứng cạnh. Nó tạo tiêu chuẩn chung cho cách chọn việc, bỏ việc và ưu tiên nguồn lực.", "Nếu lãnh đạo chỉ nói mục tiêu số, nhân viên có thể đạt số bằng cách làm hỏng văn hóa. Nếu lãnh đạo nói rõ lý do, đội ngũ có cơ sở để tự điều chỉnh.", "Why tốt nhất không nằm trên tường, mà xuất hiện trong quyết định khó."),
        Chapter(4, "Giữ Sự Nhất Quán", 4, "Một tổ chức mất niềm tin khi What không còn phản ánh Why. Nói vì người dùng nhưng thiết kế gây nghiện, nói vì chất lượng nhưng cắt góc quá mức, đều làm thương hiệu rỗng đi.", "Sự nhất quán cần kỷ luật. Mỗi sản phẩm, chiến dịch và chính sách là một lần chứng minh hoặc làm yếu lý do tồn tại.", "Vì vậy Start With Why không chỉ là cách nói, mà là cách sống với lời hứa của mình.")
    ];

    private static readonly List<string> LeanTakeaways = ["Startup nên kiểm chứng giả định, không chỉ xây theo niềm tin.", "MVP giúp học nhanh với chi phí thấp.", "Build-measure-learn là vòng lặp trung tâm của phát triển sản phẩm.", "Validated learning quan trọng hơn cảm giác bận rộn.", "Pivot là thay đổi chiến lược có kỷ luật khi dữ liệu cho thấy hướng cũ sai."];
    private static readonly List<SeedChapter> LeanChapters = [
        Chapter(1, "Học Trước Khi Mở Rộng", 5, "Ries cho rằng rủi ro lớn nhất của startup không phải là xây chậm, mà là xây rất nhanh một thứ không ai cần.", "Vì vậy mục tiêu ban đầu không phải hoàn hảo hóa sản phẩm, mà là học xem giả định nào đúng. Mỗi tính năng nên được xem như một thí nghiệm.", "Cách nhìn này giúp nhóm bớt lãng phí tháng trời vào cảm giác tiến độ giả."),
        Chapter(2, "MVP và Thí Nghiệm", 5, "MVP là phiên bản nhỏ nhất giúp kiểm chứng một giả định quan trọng. Nó không phải sản phẩm xấu, mà là công cụ học tập có chủ đích.", "Một MVP tốt trả lời câu hỏi rõ ràng: người dùng có thật sự muốn giá trị này không, họ có dùng lại không, họ có trả tiền hoặc đánh đổi gì không.", "Nếu MVP không cho dữ liệu học được, nó chỉ là bản demo nhỏ."),
        Chapter(3, "Build - Measure - Learn", 6, "Vòng lặp trung tâm của Lean Startup là xây, đo và học. Nhưng thứ tự tư duy nên bắt đầu từ điều cần học, sau đó chọn chỉ số cần đo, cuối cùng mới quyết định xây gì.", "Chỉ số tốt phải phản ánh hành vi thật. Lượt xem hay số đăng ký có thể gây vui, nhưng chưa chắc nói lên giá trị nếu người dùng không quay lại.", "Một nhóm tốt rút ngắn vòng lặp này để sai nhanh, học nhanh và điều chỉnh trước khi hết nguồn lực."),
        Chapter(4, "Pivot Có Kỷ Luật", 5, "Pivot không phải đổi ý tùy hứng. Nó là thay đổi chiến lược dựa trên điều đã học, trong khi vẫn giữ một phần tầm nhìn.", "Có nhiều dạng pivot: đổi phân khúc khách hàng, đổi kênh, đổi mô hình doanh thu hoặc tập trung vào một tính năng được yêu thích.", "Điểm quan trọng là can đảm thừa nhận dữ liệu. Bám vào kế hoạch sai chỉ vì đã đầu tư nhiều là một kiểu lãng phí khác.")
    ];

    private static readonly List<string> GreatTakeaways = ["Công ty vĩ đại bắt đầu bằng người phù hợp trước chiến lược chi tiết.", "Lãnh đạo cấp độ 5 kết hợp khiêm tốn cá nhân và ý chí nghề nghiệp.", "Đối diện sự thật tàn nhẫn nhưng không mất niềm tin.", "Khái niệm con nhím giúp tập trung vào giao điểm năng lực, kinh tế và đam mê.", "Kỷ luật nhất quán quan trọng hơn một khoảnh khắc bứt phá."];
    private static readonly List<SeedChapter> GreatChapters = [
        Chapter(1, "Lãnh Đạo Cấp Độ 5", 6, "Collins mô tả lãnh đạo cấp độ 5 là người vừa khiêm tốn vừa quyết liệt. Họ không xây tổ chức quanh cái tôi cá nhân, nhưng lại có ý chí mạnh mẽ để làm điều khó.", "Kiểu lãnh đạo này thường ít hào nhoáng. Họ chia công lao cho đội ngũ và nhận trách nhiệm khi kết quả xấu.", "Điều này tạo nền văn hóa bền hơn, vì tổ chức không phụ thuộc hoàn toàn vào sự tỏa sáng của một cá nhân."),
        Chapter(2, "Đúng Người Trước, Đúng Việc Sau", 6, "Một ý tưởng nổi tiếng là đưa đúng người lên xe trước khi quyết định lái xe đi đâu. Người phù hợp có kỷ luật nội tại và không cần bị quản lý bằng mệnh lệnh liên tục.", "Chiến lược có thể thay đổi, nhưng đội ngũ tốt giúp tổ chức thích nghi. Ngược lại, chiến lược hay khó cứu một đội ngũ thiếu tin cậy.", "Điều này không chỉ áp dụng cho công ty lớn. Một dự án nhỏ cũng cần người có trách nhiệm, năng lực và cùng tiêu chuẩn làm việc."),
        Chapter(3, "Sự Thật Tàn Nhẫn và Niềm Tin", 6, "Các công ty tốt không né tránh dữ liệu xấu. Họ tạo môi trường nơi sự thật có thể được nói ra mà không bị trừng phạt.", "Nhưng đối diện thực tế không đồng nghĩa với bi quan. Collins gọi đây là nghịch lý Stockdale: nhìn thẳng hoàn cảnh khắc nghiệt trong khi vẫn giữ niềm tin cuối cùng.", "Sự kết hợp này giúp tổ chức không tự ru ngủ nhưng cũng không sụp đổ tinh thần."),
        Chapter(4, "Khái Niệm Con Nhím", 6, "Khái niệm con nhím là giao điểm của ba câu hỏi: bạn có thể giỏi nhất ở điều gì, điều gì vận hành động cơ kinh tế, và điều gì khiến bạn thật sự đam mê.", "Tập trung vào giao điểm này giúp tổ chức bớt chạy theo cơ hội rời rạc. Nhiều chiến lược thất bại vì cố làm quá nhiều thứ trông hấp dẫn nhưng không nằm trong lõi năng lực.", "Sự vĩ đại, theo Collins, thường đến từ kỷ luật lâu dài hơn là một quyết định ngoạn mục.")
    ];

    private static readonly List<string> HabitsTakeaways = ["Sống chủ động nghĩa là nhận trách nhiệm với phản ứng của mình.", "Bắt đầu với đích đến giúp quyết định hằng ngày có hướng.", "Ưu tiên điều quan trọng trước điều khẩn cấp giả.", "Tư duy cùng thắng tạo hợp tác bền hơn thắng thua.", "Lắng nghe để hiểu là nền tảng của ảnh hưởng."];
    private static readonly List<SeedChapter> HabitsChapters = [
        Chapter(1, "Từ Phụ Thuộc Đến Chủ Động", 6, "Covey bắt đầu bằng việc phân biệt giữa hoàn cảnh và phản ứng. Con người không kiểm soát được mọi thứ xảy ra, nhưng có khoảng tự do để chọn phản ứng.", "Sống chủ động không phải lúc nào cũng kiểm soát kết quả. Nó là nhận trách nhiệm với phần mình có thể ảnh hưởng.", "Khi tập trung vào vòng ảnh hưởng thay vì vòng quan tâm, năng lượng cá nhân được dùng hiệu quả hơn."),
        Chapter(2, "Bắt Đầu Với Đích Đến", 5, "Thói quen này yêu cầu người đọc hình dung mình muốn trở thành ai trước khi tối ưu lịch trình. Nếu không có đích đến, năng suất chỉ khiến ta đi nhanh hơn về hướng không chắc đúng.", "Covey khuyên xây tuyên ngôn cá nhân dựa trên giá trị. Nó trở thành tiêu chuẩn cho quyết định khó.", "Điều này giúp người đọc phân biệt thành công bề ngoài với đời sống thật sự phù hợp."),
        Chapter(3, "Điều Quan Trọng Trước", 6, "Nhiều người bị cuốn vào việc khẩn cấp: email, yêu cầu tức thì, vấn đề nhỏ nhưng ồn. Covey nhấn mạnh vùng quan trọng nhưng không khẩn cấp: chuẩn bị, xây quan hệ, học tập và phòng ngừa.", "Nếu bỏ quên vùng này, cuộc sống sẽ ngày càng nhiều khẩn cấp thật. Ngược lại, đầu tư vào điều quan trọng làm giảm khủng hoảng về sau.", "Quản lý thời gian vì vậy là quản lý ưu tiên và can đảm nói không."),
        Chapter(4, "Từ Độc Lập Đến Hợp Tác", 5, "Ba thói quen sau nói về quan hệ: cùng thắng, lắng nghe để hiểu và hiệp lực. Covey xem trưởng thành không dừng ở độc lập, mà tiến tới phụ thuộc lẫn nhau một cách lành mạnh.", "Lắng nghe thật sự là kỹ năng khó vì ta thường nghe để trả lời. Khi người khác cảm thấy được hiểu, họ ít phòng thủ hơn.", "Hiệp lực xuất hiện khi khác biệt không bị xem là mối đe dọa mà là nguồn tạo giải pháp tốt hơn.")
    ];

    private static readonly List<string> MindsetTakeaways = ["Tư duy cố định xem năng lực là thứ phải chứng minh.", "Tư duy phát triển xem năng lực là thứ có thể rèn luyện.", "Khen nỗ lực và chiến lược tốt hơn khen tài năng cố định.", "Thất bại là phản hồi nếu người học còn tin mình có thể cải thiện.", "Môi trường học tập nên thưởng cho tiến bộ, không chỉ thành tích."];
    private static readonly List<SeedChapter> MindsetChapters = [
        Chapter(1, "Hai Kiểu Tư Duy", 5, "Dweck phân biệt tư duy cố định và tư duy phát triển. Người có tư duy cố định xem năng lực như thứ đã được định sẵn, nên mỗi thử thách trở thành bài kiểm tra giá trị bản thân.", "Người có tư duy phát triển tin rằng năng lực có thể cải thiện qua nỗ lực, chiến lược và phản hồi. Họ vẫn có thể thất vọng khi thất bại, nhưng ít xem thất bại là dấu chấm hết.", "Sự khác biệt này ảnh hưởng đến cách học, làm việc và yêu thương."),
        Chapter(2, "Thất Bại và Danh Tính", 5, "Trong tư duy cố định, thất bại dễ bị hiểu là bằng chứng mình không đủ giỏi. Điều này khiến người ta tránh thử thách để bảo vệ hình ảnh.", "Trong tư duy phát triển, thất bại là thông tin. Nó cho thấy chiến lược hiện tại chưa phù hợp hoặc kỹ năng còn thiếu.", "Sự chuyển đổi quan trọng là từ câu hỏi mình có thông minh không sang câu hỏi mình cần học gì tiếp theo."),
        Chapter(3, "Khen Ngợi và Môi Trường", 4, "Dweck cảnh báo việc khen trẻ là thông minh có thể khiến trẻ sợ mất nhãn thông minh. Khen nỗ lực, chiến lược và sự kiên trì giúp trẻ gắn thành công với quá trình có thể kiểm soát.", "Điều này cũng đúng trong tổ chức. Một văn hóa chỉ tôn vinh người luôn đúng sẽ khiến nhân viên che giấu sai lầm.", "Văn hóa phát triển khuyến khích thử nghiệm, phản hồi và cải thiện liên tục."),
        Chapter(4, "Ứng Dụng Trong Đời Sống", 4, "Tư duy phát triển không phải là tin ai cũng có thể làm mọi thứ ngang nhau. Nó là tin rằng con người có thể tiến bộ có ý nghĩa khi có phương pháp và thời gian.", "Trong quan hệ, nó giúp ta bớt đóng khung người khác bằng lỗi lầm cũ. Trong công việc, nó giúp ta nhận phản hồi mà không tan vỡ.", "Giá trị của sách nằm ở việc biến học tập thành một phần của bản sắc.")
    ];

    private static readonly List<string> NowTakeaways = ["Nhiều đau khổ đến từ việc đồng nhất bản thân với dòng suy nghĩ.", "Hiện tại là nơi duy nhất ta có thể hành động thật sự.", "Quan sát cảm xúc giúp ta bớt bị chúng điều khiển.", "Chấp nhận không phải bỏ cuộc mà là nhìn rõ thực tại trước khi đáp ứng.", "Sự hiện diện cần luyện tập qua những khoảnh khắc nhỏ."];
    private static readonly List<SeedChapter> NowChapters = [
        Chapter(1, "Tâm Trí Không Ngừng Nói", 4, "Tolle cho rằng phần lớn con người bị cuốn vào dòng suy nghĩ liên tục. Ta không chỉ có suy nghĩ; ta thường đồng nhất mình với suy nghĩ.", "Khi một ý nghĩ lo âu xuất hiện, ta dễ tin nó là sự thật toàn bộ. Sách mời người đọc quan sát suy nghĩ như hiện tượng đi qua.", "Khoảng cách nhỏ giữa người quan sát và dòng nghĩ tạo ra tự do nội tâm."),
        Chapter(2, "Trở Về Hiện Tại", 5, "Hiện tại không chỉ là khái niệm tinh thần. Nó là nơi cơ thể đang thở, âm thanh đang vang và hành động thật có thể xảy ra.", "Lo âu thường kéo ta về tương lai tưởng tượng; tiếc nuối kéo ta về quá khứ đã qua. Quay về hiện tại giúp tâm trí bớt bị hai lực kéo này xé nhỏ.", "Thực hành có thể rất đơn giản: cảm nhận bàn chân, hơi thở, hoặc việc đang làm ngay trước mắt."),
        Chapter(3, "Chấp Nhận và Hành Động", 4, "Chấp nhận trong sách không phải cam chịu. Nó là ngừng phủ nhận thực tại đủ lâu để nhìn rõ mình đang đối diện điều gì.", "Khi không còn tiêu hao năng lượng vào câu hỏi tại sao chuyện này lại xảy ra với mình, ta có nhiều năng lượng hơn để chọn phản ứng.", "Sự hiện diện vì vậy không tách khỏi hành động. Nó làm hành động bớt bị dẫn bởi hoảng loạn."),
        Chapter(4, "Thực Hành Trong Đời Sống", 4, "The Power of Now dễ bị hiểu như lời mời rời bỏ đời thường, nhưng giá trị thực tế nằm ở các khoảnh khắc nhỏ: rửa bát, nghe người khác nói, đi bộ, làm việc.", "Mỗi khoảnh khắc là cơ hội nhận ra mình đã trôi đi và quay lại. Không cần hoàn hảo; chỉ cần quay lại nhiều lần.", "Theo thời gian, sự quay lại ấy tạo một kiểu bình an không phụ thuộc hoàn toàn vào hoàn cảnh.")
    ];

    private static readonly List<string> EssentialTakeaways = ["Không phải việc nào cũng quan trọng như nhau.", "Nói không là kỹ năng bảo vệ điều thật sự cần thiết.", "Khoảng trống suy nghĩ giúp nhận ra ưu tiên thật.", "Làm ít hơn nhưng tốt hơn tạo tiến bộ rõ hơn.", "Ranh giới tốt giúp năng lượng không bị chia vụn."];
    private static readonly List<SeedChapter> EssentialChapters = [
        Chapter(1, "Theo Đuổi Ít Hơn", 4, "McKeown cho rằng nhiều người không thiếu nỗ lực, họ thiếu chọn lọc. Khi mọi thứ đều quan trọng, không còn gì thật sự quan trọng.", "Essentialism bắt đầu bằng việc chấp nhận đánh đổi. Chọn một việc nghĩa là không chọn nhiều việc khác.", "Sự trưởng thành nằm ở việc chọn có ý thức thay vì bị lịch của người khác chọn hộ."),
        Chapter(2, "Phân Biệt Tín Hiệu và Tiếng Ồn", 5, "Để biết điều gì thiết yếu, người đọc cần khoảng trống quan sát. Lịch quá kín khiến mọi yêu cầu đều có vẻ khẩn cấp.", "McKeown khuyên đặt tiêu chuẩn cao cho việc nói có. Nếu một cơ hội không rõ ràng là rất phù hợp, nó có thể là không.", "Tiêu chuẩn này giúp giảm những cam kết trung bình đang ăn mất năng lượng của cam kết quan trọng."),
        Chapter(3, "Nghệ Thuật Nói Không", 5, "Nói không thường khó vì ta sợ làm người khác thất vọng. Nhưng nói có quá nhiều cuối cùng cũng làm người khác thất vọng khi chất lượng giảm.", "Một lời từ chối tốt nên rõ, tôn trọng và không cần giải thích quá mức. Nó bảo vệ năng lượng cho điều ta đã cam kết sâu.", "Ranh giới không phải ích kỷ; nó là điều kiện để đóng góp tốt."),
        Chapter(4, "Thực Thi Điều Thiết Yếu", 4, "Khi đã chọn điều quan trọng, hãy làm nó dễ thực hiện bằng thói quen, lịch trình và việc loại bỏ ma sát.", "Essentialism không chỉ là triết lý tối giản. Nó là hệ thống để điều quan trọng có không gian thật trong đời sống.", "Kết quả là tiến bộ chậm hơn bề ngoài nhưng chắc hơn bên trong.")
    ];

    private static readonly List<string> RangeTakeaways = ["Chuyên môn sớm không phải lúc nào cũng tối ưu.", "Môi trường phức tạp cần khả năng liên tưởng rộng.", "Thử nhiều lĩnh vực giúp tìm điểm phù hợp tốt hơn.", "Kỹ năng từ lĩnh vực này có thể chuyển sang lĩnh vực khác.", "Học sâu và học rộng cần được cân bằng theo bối cảnh."];
    private static readonly List<SeedChapter> RangeChapters = [
        Chapter(1, "Huyền Thoại Chuyên Môn Sớm", 5, "Epstein phản biện câu chuyện rằng mọi thành công đều đến từ chuyên môn hóa từ nhỏ. Trong một số lĩnh vực như cờ vua hay golf, phản hồi rõ ràng khiến chuyên môn sớm rất hữu ích.", "Nhưng nhiều lĩnh vực đời thực không có quy tắc ổn định như vậy. Kinh doanh, khoa học, thiết kế và lãnh đạo thường có vấn đề mơ hồ.", "Trong môi trường mơ hồ, trải nghiệm rộng giúp con người nhìn vấn đề từ nhiều góc hơn."),
        Chapter(2, "Môi Trường Tử Tế và Môi Trường Phức Tạp", 5, "Môi trường tử tế có quy tắc rõ, phản hồi nhanh và mẫu lặp lại. Ở đó, luyện tập chuyên sâu sớm tạo lợi thế lớn.", "Môi trường phức tạp có phản hồi chậm, dữ liệu nhiễu và tình huống mới. Ở đó, chỉ lặp lại một kỹ năng có thể làm ta quá hẹp.", "Range giúp người đọc tự hỏi mình đang chơi trong loại môi trường nào trước khi chọn chiến lược học."),
        Chapter(3, "Sức Mạnh Của Liên Tưởng", 5, "Người có trải nghiệm rộng thường có nhiều mô hình tinh thần để mượn. Một ý tưởng từ sinh học có thể giúp giải bài toán công nghệ; một nguyên tắc từ âm nhạc có thể giúp thiết kế sản phẩm.", "Sự sáng tạo nhiều khi là kết nối giữa những vùng tưởng không liên quan. Vì vậy thời gian khám phá không nhất thiết là lãng phí.", "Điều quan trọng là biến trải nghiệm rộng thành hiểu biết có cấu trúc, không chỉ nhảy việc hoặc đổi sở thích liên tục."),
        Chapter(4, "Tìm Điểm Phù Hợp", 5, "Epstein nhấn mạnh giai đoạn thử nghiệm giúp con người tìm môi trường phù hợp với năng lực và hứng thú. Chọn quá sớm có thể khiến ta mắc kẹt trong con đường không hợp.", "Sự nghiệp hiện đại thường không tuyến tính. Người học rộng có thể đến muộn nhưng mang theo lợi thế kết hợp.", "Bài học không phải là chống chuyên môn, mà là chuyên môn sau khi đã đủ khám phá để chọn đúng hướng.")
    ];

    private static readonly List<string> LeadTakeaways = ["Lãnh đạo can đảm cần khả năng nói thật trong tình huống khó.", "Tính dễ tổn thương không đối lập với sức mạnh.", "Niềm tin được xây bằng hành động nhỏ và nhất quán.", "Phản hồi rõ ràng tốt hơn sự tử tế mơ hồ.", "Văn hóa tốt cho phép học từ sai lầm thay vì che giấu."];
    private static readonly List<SeedChapter> LeadChapters = [
        Chapter(1, "Can Đảm Trong Lãnh Đạo", 5, "Brené Brown cho rằng lãnh đạo không chỉ là chức danh mà là khả năng bước vào tình huống không chắc chắn với sự chính trực.", "Can đảm không có nghĩa là không sợ. Nó là dám nói điều cần nói, hỏi điều khó và chịu trách nhiệm khi kết quả chưa rõ.", "Một đội ngũ cần kiểu can đảm này để không bị mắc kẹt trong né tránh và giả vờ ổn."),
        Chapter(2, "Tính Dễ Tổn Thương", 5, "Tính dễ tổn thương thường bị hiểu nhầm là yếu đuối. Brown định nghĩa nó là sự sẵn sàng xuất hiện khi không kiểm soát được kết quả.", "Trong công việc, điều này có thể là thừa nhận mình chưa biết, xin phản hồi hoặc nói rõ một rủi ro bị bỏ qua.", "Khi lãnh đạo làm được điều đó một cách có trách nhiệm, đội ngũ học rằng sự thật quan trọng hơn hình ảnh hoàn hảo."),
        Chapter(3, "Xây Niềm Tin", 5, "Niềm tin không được tạo bằng một bài phát biểu lớn. Nó được xây bằng những hành động nhỏ: giữ lời, tôn trọng ranh giới, nói rõ kỳ vọng và sửa sai khi làm hỏng.", "Brown nhấn mạnh rằng niềm tin cũng cần sự rõ ràng. Mập mờ thường tạo lo lắng và diễn giải tiêu cực.", "Một văn hóa tin cậy không tránh xung đột; nó xử lý xung đột bằng sự tôn trọng."),
        Chapter(4, "Phản Hồi và Trách Nhiệm", 4, "Phản hồi tốt cần vừa trung thực vừa nhân văn. Sự tử tế mơ hồ khiến người nhận không biết phải sửa gì; sự thẳng thừng thiếu tôn trọng khiến họ phòng thủ.", "Dare to Lead khuyến khích chuẩn bị cho các cuộc trò chuyện khó thay vì trì hoãn. Trách nhiệm rõ ràng giúp đội ngũ trưởng thành.", "Lãnh đạo tốt không loại bỏ khó chịu, mà biến khó chịu thành nơi học tập.")
    ];

    private static readonly List<string> DesignTakeaways = ["Lỗi người dùng thường là dấu hiệu của thiết kế kém.", "Affordance và signifier giúp người dùng hiểu có thể làm gì.", "Phản hồi rõ ràng giúp người dùng biết hành động đã có tác dụng.", "Ràng buộc tốt ngăn lỗi trước khi lỗi xảy ra.", "Thiết kế tốt làm hệ thống dễ hiểu thay vì bắt người dùng ghi nhớ."];
    private static readonly List<SeedChapter> DesignChapters = [
        Chapter(1, "Khi Người Dùng Tự Trách Mình", 5, "Norman chỉ ra rằng khi không mở được cửa, dùng sai máy giặt hoặc bấm nhầm nút, người dùng thường tự trách mình. Nhưng rất nhiều lỗi đến từ thiết kế không rõ.", "Một đồ vật tốt cho thấy cách dùng thông qua hình dạng, vị trí và phản hồi. Nếu người dùng phải đoán quá nhiều, hệ thống đang đẩy gánh nặng nhận thức sang họ.", "Tư duy này rất quan trọng với phần mềm: giao diện tốt không khiến người dùng cảm thấy ngu."),
        Chapter(2, "Affordance và Signifier", 5, "Affordance là khả năng hành động mà vật thể gợi ra; signifier là dấu hiệu chỉ cho người dùng biết hành động đó. Một tay nắm gợi kéo, một mặt phẳng gợi đẩy, một nút nổi gợi bấm.", "Trong giao diện số, signifier có thể là màu, viền, nhãn, icon hoặc vị trí. Nếu mọi thứ trông giống nhau, người dùng không biết đâu là hành động chính.", "Thiết kế tốt không cần giải thích dài vì bản thân nó đã nói đủ rõ."),
        Chapter(3, "Phản Hồi và Ánh Xạ", 5, "Sau khi hành động, người dùng cần biết hệ thống đã nhận lệnh chưa. Một âm báo, trạng thái loading, thay đổi màu hoặc thông báo thành công đều là phản hồi.", "Ánh xạ là mối quan hệ giữa điều khiển và kết quả. Bếp có núm xoay đặt đúng vị trí vùng nấu sẽ dễ hiểu hơn núm xếp hàng thẳng không liên quan.", "Trong phần mềm, ánh xạ tốt giúp người dùng dự đoán kết quả trước khi nhấn."),
        Chapter(4, "Thiết Kế Để Ngăn Lỗi", 6, "Norman cho rằng thiết kế nên phòng lỗi, không chỉ báo lỗi. Ràng buộc vật lý, logic hoặc giao diện có thể ngăn người dùng chọn sai trước khi sai lầm xảy ra.", "Khi lỗi vẫn xảy ra, thông báo nên giúp sửa, không chỉ đổ lỗi. Một thông báo tốt nói điều gì sai, vì sao quan trọng và người dùng có thể làm gì tiếp theo.", "Bài học cuối cùng là thiết kế tốt có đạo đức: nó tôn trọng sự chú ý, thời gian và cảm giác năng lực của người dùng.")
    ];

    private const string LightbulbSvg = """<svg viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2a7 7 0 0 1 7 7c0 2.38-1.19 4.47-3 5.74V17a2 2 0 0 1-2 2h-4a2 2 0 0 1-2-2v-2.26C6.19 13.47 5 11.38 5 9a7 7 0 0 1 7-7z"/><line x1="9" y1="21" x2="15" y2="21"/><line x1="10" y1="17" x2="10" y2="21"/><line x1="14" y1="17" x2="14" y2="21"/></svg>""";
    private const string GlobeSvg = """<svg viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><line x1="2" y1="12" x2="22" y2="12"/><path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/></svg>""";
    private const string BoltSvg = """<svg viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><polygon points="13 2 3 14 12 14 11 22 21 10 12 10 13 2"/></svg>""";
    private const string UsersSvg = """<svg viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M23 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/></svg>""";
    private const string LayersSvg = """<svg viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 1 1 7l11 6 11-6-11-6z"/><path d="M1 13l11 6 11-6"/><path d="M1 19l11 6 11-6"/></svg>""";
    private const string SmileSvg = """<svg viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M8 14s1.5 2 4 2 4-2 4-2"/><line x1="9" y1="9" x2="9.01" y2="9"/><line x1="15" y1="9" x2="15.01" y2="9"/></svg>""";
    private const string TargetSvg = """<svg viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><circle cx="12" cy="12" r="6"/><circle cx="12" cy="12" r="2"/></svg>""";
    private const string RocketSvg = """<svg viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M4.5 16.5c-1.5 1.26-2 5-2 5s3.74-.5 5-2c.71-.84.7-2.13-.09-2.91a2.18 2.18 0 0 0-2.91-.09z"/><path d="M12 15l-3-3a22 22 0 0 1 2-3.95A12.88 12.88 0 0 1 22 2c0 2.72-.78 7.5-6 11a22.35 22.35 0 0 1-4 2z"/></svg>""";
    private const string DollarSvg = """<svg viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><line x1="12" y1="1" x2="12" y2="23"/><path d="M17 5H9.5a3.5 3.5 0 0 0 0 7h5a3.5 3.5 0 0 1 0 7H6"/></svg>""";
    private const string ChartSvg = """<svg viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><line x1="18" y1="20" x2="18" y2="10"/><line x1="12" y1="20" x2="12" y2="4"/><line x1="6" y1="20" x2="6" y2="14"/></svg>""";
    private const string LeafSvg = """<svg viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M17 8C8 10 5.9 16.17 3.82 21.34l1.89.66.95-2.3c.48.17.98.3 1.34.3C19 20 22 3 22 3c-1 2-8 2.25-13 3.25S2 11.5 2 13.5s1.75 3.75 1.75 3.75"/></svg>""";

    private sealed record SeedBook(string Title, string Slug, string Author, string Category, decimal Rating, int ReadingTimeMinutes, string CoverGradient, string CoverSvg, string CoverUrl, string Description, string Introduction, List<string> Takeaways, List<SeedChapter> Chapters);

    private sealed record SeedChapter(int Number, string Title, int ReadingTimeMinutes, string ContentHtml);
}
