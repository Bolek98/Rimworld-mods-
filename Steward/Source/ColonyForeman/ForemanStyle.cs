namespace ColonyForeman;

// New values are appended, so saved maps keep their style.
public enum ForemanStyle
{
	Balanced,
	Survival,
	Production,
	Research,
	Farming,
	Building,
	Industry,
	Defense,
	Care,
	Comfort,
	Trade,
	Ranching,
	// 2.12: your own style – uses the importance, min/max colonists and
	// colonists-per-level from the mod settings.
	Custom,
	// 2.38: hauling and cleaning only
	Tidy,
	// 2.41: fields and forest only (growing + plant cutting)
	FieldsForest,
	// 2.42: more single-purpose styles
	Mining,
	Kitchen,
	HuntFish,
	Tailoring,
	Smithing,
	Art,
	Prison
}
