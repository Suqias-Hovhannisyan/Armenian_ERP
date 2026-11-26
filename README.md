# Armenia Retail ERP

Սա զրոյից կառուցված առաջին մշակման տարբերակն է մեկ սուպերմարկետի համար։ Այս տարբերակով կարելի է ստեղծել ապրանք, գրանցել պահեստի մուտք, կատարել վաճառք և նույն վաճառքից մասնակի վերադարձ։ Յուրաքանչյուր փաստաթուղթ այս տարբերակում մեկ ապրանքի համար է։

## Տեղային գործարկում Windows-ում

Պահանջվում են .NET 10 SDK, SQL Server և SQL Server Management Studio կամ sqlcmd։

1. Ստեղծիր դատարկ փորձնական SQL Server բազա, օրինակ՝ `ArmeniaRetailERP_Dev`։
2. Բացիր և գործարկիր `src/Retail.Api/Database/001-initial.sql` ֆայլը այդ բազայի վրա։
3. PowerShell-ում նախագծի պանակից գործարկիր API-ն։ Օրինակը Windows-ի տեղային SQLEXPRESS-ի համար է։ Այլ instance-ի դեպքում փոխիր Server արժեքը։

```powershell
$env:ConnectionStrings__Retail = 'Server=localhost\SQLEXPRESS;Database=ArmeniaRetailERP_Dev;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
dotnet run --project src/Retail.Api --no-launch-profile --urls http://localhost:5080
```

TrustServerCertificate պարամետրը նախատեսված է այստեղ նշված տեղային փորձնական բազայի համար։

4. Երկրորդ PowerShell պատուհանում գործարկիր Web Admin-ը և բացիր [http://localhost:5081](http://localhost:5081)։

```powershell
$env:RetailApi__BaseUrl = 'http://localhost:5080'
dotnet run --project src/Retail.Admin --no-launch-profile --urls http://localhost:5081
```

5. Python 3-ով գործարկիր API փորձարկումը։

```powershell
python tests/smoke.py http://localhost:5080
```

Փորձարկումը ստեղծում է նոր ապրանք 450 AMD գնով, մուտքագրում 10 միավոր, վաճառում 3 և վերադարձնում 1։ Վերջնական մնացորդը պետք է լինի 8։ Ստուգվում են նաև կրկնակի ուղարկումը, նույն հարցման ID-ով տարբեր տվյալների ուղարկումը, մնացորդից ավելի վաճառքի և վաճառված քանակից ավելի վերադարձի մերժումը։ Փորձնական ապրանքն ու գործարքները մնում են բազայում։

## Թողարկման սահմանները

Այս փաթեթը նախատեսված է տեղային մշակման և փորձարկման համար։ Աշխատեցրու միայն տեղային հասցեով, քանի որ այս փուլում API-ն ու Web Admin-ը դեռ չունեն օգտատերերի մուտք կամ դերային պաշտպանություն։ Հաջորդ փուլերն են մուտքի իրավունքները, բազմատող փաստաթղթերը, խմբաքանակներն ու ժամկետները, WPF դրամարկղը և SQLite սինքրոնացումը, հերթափոխը, վճարային, ՀԴՄ և Data Matrix ինտեգրումները։ Հայաստանի մանրածախ վաճառքի համար արտադրական ընդունումը պահանջում է իրական ինտեգրացիաների ու սարքավորումների փորձարկում։
