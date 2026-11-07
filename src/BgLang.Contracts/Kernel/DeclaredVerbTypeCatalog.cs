using Aspect = BgLang.Contracts.MorphologicalVerbAspect;
using Transitivity = BgLang.Contracts.MorphologicalVerbTransitivity;

namespace BgLang.Contracts;

// v1 projects the stored Name and group of the pinned Legacy types.persistent.
// Source (93236 bytes) SHA-256 185a19944ee998d787687f88356b39a7cd2c12f207f5ab75c03eecd2f50d24bb
// Canonical verb table (ID, Name, group) SHA-256 296e3683d9eed50f670edd5f6a35ccdaf17dd6e49e6d0646f5de4bdebd55ba93
// The four generic records (142, 143, 144, idi303) have no declared axes.
internal static class DeclaredVerbTypeCatalog
{
    // String patterns match the entire Name using ordinal equality.
    internal static (Transitivity Transitivity, Aspect Aspect)? Lookup(string? paradigmId) => paradigmId switch
    {
        "145bii" or "145ii" or "148ii" or "150ii" or "151ii" or "152ii" or "154ii" or "155ii" or
        "160aii" or "160ii" or "161ii" or "162ii" or "163ii" or "164ii" or "165" or "166" or
        "173ii" or "174ii" or "175ii" or "176aii" or "176ii" or "177ii" or "178aii" or "178bii" or
        "178ii" or "180ii" or "182ii" or "184aii" or "186ii" or "186ii2" or "187ii" => (Transitivity.Intransitive, Aspect.Imperfective),
        "145ait" or "145bit" or "145it" or "146" or "146a" or "147" or "148it" or "150it" or
        "151it" or "152ait" or "152it" or "153it" or "154it" or "155it" or "156it" or "159it" or
        "160ait" or "160it" or "161it" or "162it" or "163it" or "164it" or "169it" or "170it" or
        "171it" or "173it" or "174it" or "175it" or "176ait" or "176bit" or "176it" or "177it" or
        "178ait" or "178it" or "179it" or "180it" or "181it" or "182it" or "184ait" or "186it" or
        "187it" => (Transitivity.Intransitive, Aspect.Perfective),
        "145ati" or "145ti" or "148ti" or "149ti" or "150ti" or "151ti" or "152ti" or "154ti" or
        "155" or "156ti" or "157ti" or "158ti" or "159ti" or "160ati" or "160ti" or "162ati" or
        "162bti" or "162ti" or "163ti" or "164ti" or "167ti" or "169ti" or "170" or "172ti" or
        "173ti" or "174ti" or "175ti" or "176ati" or "176bti" or "176ti" or "177ti" or "178cti" or
        "178ti" or "179" or "183ti" or "184ti" or "185ti" or "186ti" or "187ti" => (Transitivity.Transitive, Aspect.Imperfective),
        "145att" or "145tt" or "148tt" or "149tt" or "150tt" or "151tt" or "152att" or "152tt" or
        "153tt" or "154tt" or "155tt" or "156tt" or "157tt" or "158tt" or "159tt" or "160att" or
        "160tt" or "161tt" or "162tt" or "163tt" or "164tt" or "167tt" or "168" or "169tt" or
        "170tt" or "171tt" or "172tt" or "173tt" or "174tt" or "175att" or "175tt" or "176att" or
        "176btt" or "176ctt" or "176tt" or "177tt" or "178btt" or "178ctt" or "178tt" or "179tt" or
        "181tt" or "183tt" or "184tt" or "185tt" or "186tt" or "187tt" => (Transitivity.Transitive, Aspect.Perfective),
        _ => null
    };
}
