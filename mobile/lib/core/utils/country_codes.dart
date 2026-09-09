/// Country metadata for the shared country-code selector.
///
/// Bundled static dataset (no runtime/network dependency) containing the
/// common English name, ISO 3166-1 alpha-2 code, flag emoji and international
/// dialing code for every sovereign state plus commonly-used territories.
class CountryCode {
  const CountryCode({
    required this.name,
    required this.isoCode,
    required this.flag,
    required this.dialCode,
  });

  /// Common English country/territory name.
  final String name;

  /// ISO 3166-1 alpha-2 code (uppercase).
  final String isoCode;

  /// Flag emoji.
  final String flag;

  /// International dialing code WITHOUT the leading `+` (e.g. `91`, `1`,
  /// `44`, `971`, `61`). North American (NANP) territories include their
  /// area code so the dial code stays unambiguous (e.g. `1268` for
  /// Antigua & Barbuda).
  final String dialCode;

  /// Dialing code with the leading `+` (e.g. `+91`).
  String get dialCodeWithPlus => '+$dialCode';

  @override
  bool operator ==(Object other) =>
      other is CountryCode &&
      other.isoCode == isoCode &&
      other.dialCode == dialCode;

  @override
  int get hashCode => Object.hash(isoCode, dialCode);
}

CountryCode _c(String name, String isoCode, String flag, String dialCode) =>
    CountryCode(name: name, isoCode: isoCode, flag: flag, dialCode: dialCode);

/// Registry of all bundled countries plus lookup/search helpers.
abstract final class CountryCodes {
  /// Default selection: India (+91).
  static const CountryCode india =
      CountryCode(name: 'India', isoCode: 'IN', flag: '🇮🇳', dialCode: '91');

  /// Alphabetical list of all supported countries and territories.
  static final List<CountryCode> all = <CountryCode>[
    _c('Afghanistan', 'AF', '🇦🇫', '93'),
    _c('Albania', 'AL', '🇦🇱', '355'),
    _c('Algeria', 'DZ', '🇩🇿', '213'),
    _c('American Samoa', 'AS', '🇦🇸', '1684'),
    _c('Andorra', 'AD', '🇦🇩', '376'),
    _c('Angola', 'AO', '🇦🇴', '244'),
    _c('Anguilla', 'AI', '🇦🇮', '1264'),
    _c('Antigua and Barbuda', 'AG', '🇦🇬', '1268'),
    _c('Argentina', 'AR', '🇦🇷', '54'),
    _c('Armenia', 'AM', '🇦🇲', '374'),
    _c('Aruba', 'AW', '🇦🇼', '297'),
    _c('Australia', 'AU', '🇦🇺', '61'),
    _c('Austria', 'AT', '🇦🇹', '43'),
    _c('Azerbaijan', 'AZ', '🇦🇿', '994'),
    _c('Bahamas', 'BS', '🇧🇸', '1242'),
    _c('Bahrain', 'BH', '🇧🇭', '973'),
    _c('Bangladesh', 'BD', '🇧🇩', '880'),
    _c('Barbados', 'BB', '🇧🇧', '1246'),
    _c('Belarus', 'BY', '🇧🇾', '375'),
    _c('Belgium', 'BE', '🇧🇪', '32'),
    _c('Belize', 'BZ', '🇧🇿', '501'),
    _c('Benin', 'BJ', '🇧🇯', '229'),
    _c('Bermuda', 'BM', '🇧🇲', '1441'),
    _c('Bhutan', 'BT', '🇧🇹', '975'),
    _c('Bolivia', 'BO', '🇧🇴', '591'),
    _c('Bosnia and Herzegovina', 'BA', '🇧🇦', '387'),
    _c('Botswana', 'BW', '🇧🇼', '267'),
    _c('Brazil', 'BR', '🇧🇷', '55'),
    _c('British Virgin Islands', 'VG', '🇻🇬', '1284'),
    _c('Brunei', 'BN', '🇧🇳', '673'),
    _c('Bulgaria', 'BG', '🇧🇬', '359'),
    _c('Burkina Faso', 'BF', '🇧🇫', '226'),
    _c('Burundi', 'BI', '🇧🇮', '257'),
    _c('Cabo Verde', 'CV', '🇨🇻', '238'),
    _c('Cambodia', 'KH', '🇰🇭', '855'),
    _c('Cameroon', 'CM', '🇨🇲', '237'),
    _c('Canada', 'CA', '🇨🇦', '1'),
    _c('Cayman Islands', 'KY', '🇰🇾', '1345'),
    _c('Central African Republic', 'CF', '🇨🇫', '236'),
    _c('Chad', 'TD', '🇹🇩', '235'),
    _c('Chile', 'CL', '🇨🇱', '56'),
    _c('China', 'CN', '🇨🇳', '86'),
    _c('Colombia', 'CO', '🇨🇴', '57'),
    _c('Comoros', 'KM', '🇰🇲', '269'),
    _c('Congo', 'CG', '🇨🇬', '242'),
    _c('Cook Islands', 'CK', '🇨🇰', '682'),
    _c('Costa Rica', 'CR', '🇨🇷', '506'),
    _c("Côte d'Ivoire", 'CI', '🇨🇮', '225'),
    _c('Croatia', 'HR', '🇭🇷', '385'),
    _c('Cuba', 'CU', '🇨🇺', '53'),
    _c('Curaçao', 'CW', '🇨🇼', '599'),
    _c('Cyprus', 'CY', '🇨🇾', '357'),
    _c('Czech Republic', 'CZ', '🇨🇿', '420'),
    _c('Democratic Republic of the Congo', 'CD', '🇨🇩', '243'),
    _c('Denmark', 'DK', '🇩🇰', '45'),
    _c('Djibouti', 'DJ', '🇩🇯', '253'),
    _c('Dominica', 'DM', '🇩🇲', '1767'),
    _c('Dominican Republic', 'DO', '🇩🇴', '1809'),
    _c('Ecuador', 'EC', '🇪🇨', '593'),
    _c('Egypt', 'EG', '🇪🇬', '20'),
    _c('El Salvador', 'SV', '🇸🇻', '503'),
    _c('Equatorial Guinea', 'GQ', '🇬🇶', '240'),
    _c('Eritrea', 'ER', '🇪🇷', '291'),
    _c('Estonia', 'EE', '🇪🇪', '372'),
    _c('Eswatini', 'SZ', '🇸🇿', '268'),
    _c('Ethiopia', 'ET', '🇪🇹', '251'),
    _c('Falkland Islands', 'FK', '🇫🇰', '500'),
    _c('Faroe Islands', 'FO', '🇫🇴', '298'),
    _c('Fiji', 'FJ', '🇫🇯', '679'),
    _c('Finland', 'FI', '🇫🇮', '358'),
    _c('France', 'FR', '🇫🇷', '33'),
    _c('French Guiana', 'GF', '🇬🇫', '594'),
    _c('French Polynesia', 'PF', '🇵🇫', '689'),
    _c('Gabon', 'GA', '🇬🇦', '241'),
    _c('Gambia', 'GM', '🇬🇲', '220'),
    _c('Georgia', 'GE', '🇬🇪', '995'),
    _c('Germany', 'DE', '🇩🇪', '49'),
    _c('Ghana', 'GH', '🇬🇭', '233'),
    _c('Gibraltar', 'GI', '🇬🇮', '350'),
    _c('Greece', 'GR', '🇬🇷', '30'),
    _c('Greenland', 'GL', '🇬🇱', '299'),
    _c('Grenada', 'GD', '🇬🇩', '1473'),
    _c('Guadeloupe', 'GP', '🇬🇵', '590'),
    _c('Guam', 'GU', '🇬🇺', '1671'),
    _c('Guatemala', 'GT', '🇬🇹', '502'),
    _c('Guernsey', 'GG', '🇬🇬', '441481'),
    _c('Guinea', 'GN', '🇬🇳', '224'),
    _c('Guinea-Bissau', 'GW', '🇬🇼', '245'),
    _c('Guyana', 'GY', '🇬🇾', '592'),
    _c('Haiti', 'HT', '🇭🇹', '509'),
    _c('Honduras', 'HN', '🇭🇳', '504'),
    _c('Hong Kong', 'HK', '🇭🇰', '852'),
    _c('Hungary', 'HU', '🇭🇺', '36'),
    _c('Iceland', 'IS', '🇮🇸', '354'),
    _c('India', 'IN', '🇮🇳', '91'),
    _c('Indonesia', 'ID', '🇮🇩', '62'),
    _c('Iran', 'IR', '🇮🇷', '98'),
    _c('Iraq', 'IQ', '🇮🇶', '964'),
    _c('Ireland', 'IE', '🇮🇪', '353'),
    _c('Isle of Man', 'IM', '🇮🇲', '441624'),
    _c('Israel', 'IL', '🇮🇱', '972'),
    _c('Italy', 'IT', '🇮🇹', '39'),
    _c('Jamaica', 'JM', '🇯🇲', '1876'),
    _c('Japan', 'JP', '🇯🇵', '81'),
    _c('Jersey', 'JE', '🇯🇪', '441534'),
    _c('Jordan', 'JO', '🇯🇴', '962'),
    _c('Kazakhstan', 'KZ', '🇰🇿', '7'),
    _c('Kenya', 'KE', '🇰🇪', '254'),
    _c('Kiribati', 'KI', '🇰🇮', '686'),
    _c('Kosovo', 'XK', '🇽🇰', '383'),
    _c('Kuwait', 'KW', '🇰🇼', '965'),
    _c('Kyrgyzstan', 'KG', '🇰🇬', '996'),
    _c('Laos', 'LA', '🇱🇦', '856'),
    _c('Latvia', 'LV', '🇱🇻', '371'),
    _c('Lebanon', 'LB', '🇱🇧', '961'),
    _c('Lesotho', 'LS', '🇱🇸', '266'),
    _c('Liberia', 'LR', '🇱🇷', '231'),
    _c('Libya', 'LY', '🇱🇾', '218'),
    _c('Liechtenstein', 'LI', '🇱🇮', '423'),
    _c('Lithuania', 'LT', '🇱🇹', '370'),
    _c('Luxembourg', 'LU', '🇱🇺', '352'),
    _c('Macau', 'MO', '🇲🇴', '853'),
    _c('Madagascar', 'MG', '🇲🇬', '261'),
    _c('Malawi', 'MW', '🇲🇼', '265'),
    _c('Malaysia', 'MY', '🇲🇾', '60'),
    _c('Maldives', 'MV', '🇲🇻', '960'),
    _c('Mali', 'ML', '🇲🇱', '223'),
    _c('Malta', 'MT', '🇲🇹', '356'),
    _c('Marshall Islands', 'MH', '🇲🇭', '692'),
    _c('Martinique', 'MQ', '🇲🇶', '596'),
    _c('Mauritania', 'MR', '🇲🇷', '222'),
    _c('Mauritius', 'MU', '🇲🇺', '230'),
    _c('Mayotte', 'YT', '🇾🇹', '262'),
    _c('Mexico', 'MX', '🇲🇽', '52'),
    _c('Micronesia', 'FM', '🇫🇲', '691'),
    _c('Moldova', 'MD', '🇲🇩', '373'),
    _c('Monaco', 'MC', '🇲🇨', '377'),
    _c('Mongolia', 'MN', '🇲🇳', '976'),
    _c('Montenegro', 'ME', '🇲🇪', '382'),
    _c('Montserrat', 'MS', '🇲🇸', '1664'),
    _c('Morocco', 'MA', '🇲🇦', '212'),
    _c('Mozambique', 'MZ', '🇲🇿', '258'),
    _c('Myanmar', 'MM', '🇲🇲', '95'),
    _c('Namibia', 'NA', '🇳🇦', '264'),
    _c('Nauru', 'NR', '🇳🇷', '674'),
    _c('Nepal', 'NP', '🇳🇵', '977'),
    _c('Netherlands', 'NL', '🇳🇱', '31'),
    _c('New Caledonia', 'NC', '🇳🇨', '687'),
    _c('New Zealand', 'NZ', '🇳🇿', '64'),
    _c('Nicaragua', 'NI', '🇳🇮', '505'),
    _c('Niger', 'NE', '🇳🇪', '227'),
    _c('Nigeria', 'NG', '🇳🇬', '234'),
    _c('Niue', 'NU', '🇳🇺', '683'),
    _c('North Korea', 'KP', '🇰🇵', '850'),
    _c('North Macedonia', 'MK', '🇲🇰', '389'),
    _c('Northern Mariana Islands', 'MP', '🇲🇵', '1670'),
    _c('Norway', 'NO', '🇳🇴', '47'),
    _c('Oman', 'OM', '🇴🇲', '968'),
    _c('Pakistan', 'PK', '🇵🇰', '92'),
    _c('Palau', 'PW', '🇵🇼', '680'),
    _c('Palestine', 'PS', '🇵🇸', '970'),
    _c('Panama', 'PA', '🇵🇦', '507'),
    _c('Papua New Guinea', 'PG', '🇵🇬', '675'),
    _c('Paraguay', 'PY', '🇵🇾', '595'),
    _c('Peru', 'PE', '🇵🇪', '51'),
    _c('Philippines', 'PH', '🇵🇭', '63'),
    _c('Poland', 'PL', '🇵🇱', '48'),
    _c('Portugal', 'PT', '🇵🇹', '351'),
    _c('Puerto Rico', 'PR', '🇵🇷', '1787'),
    _c('Qatar', 'QA', '🇶🇦', '974'),
    _c('Réunion', 'RE', '🇷🇪', '262'),
    _c('Romania', 'RO', '🇷🇴', '40'),
    _c('Russia', 'RU', '🇷🇺', '7'),
    _c('Rwanda', 'RW', '🇷🇼', '250'),
    _c('Saint Helena', 'SH', '🇸🇭', '290'),
    _c('Saint Kitts and Nevis', 'KN', '🇰🇳', '1869'),
    _c('Saint Lucia', 'LC', '🇱🇨', '1758'),
    _c('Saint Pierre and Miquelon', 'PM', '🇵🇲', '508'),
    _c('Saint Vincent and the Grenadines', 'VC', '🇻🇨', '1784'),
    _c('Samoa', 'WS', '🇼🇸', '685'),
    _c('San Marino', 'SM', '🇸🇲', '378'),
    _c('São Tomé and Príncipe', 'ST', '🇸🇹', '239'),
    _c('Saudi Arabia', 'SA', '🇸🇦', '966'),
    _c('Senegal', 'SN', '🇸🇳', '221'),
    _c('Serbia', 'RS', '🇷🇸', '381'),
    _c('Seychelles', 'SC', '🇸🇨', '248'),
    _c('Sierra Leone', 'SL', '🇸🇱', '232'),
    _c('Singapore', 'SG', '🇸🇬', '65'),
    _c('Sint Maarten', 'SX', '🇸🇽', '1721'),
    _c('Slovakia', 'SK', '🇸🇰', '421'),
    _c('Slovenia', 'SI', '🇸🇮', '386'),
    _c('Solomon Islands', 'SB', '🇸🇧', '677'),
    _c('Somalia', 'SO', '🇸🇴', '252'),
    _c('South Africa', 'ZA', '🇿🇦', '27'),
    _c('South Korea', 'KR', '🇰🇷', '82'),
    _c('South Sudan', 'SS', '🇸🇸', '211'),
    _c('Spain', 'ES', '🇪🇸', '34'),
    _c('Sri Lanka', 'LK', '🇱🇰', '94'),
    _c('Sudan', 'SD', '🇸🇩', '249'),
    _c('Suriname', 'SR', '🇸🇷', '597'),
    _c('Sweden', 'SE', '🇸🇪', '46'),
    _c('Switzerland', 'CH', '🇨🇭', '41'),
    _c('Syria', 'SY', '🇸🇾', '963'),
    _c('Taiwan', 'TW', '🇹🇼', '886'),
    _c('Tajikistan', 'TJ', '🇹🇯', '992'),
    _c('Tanzania', 'TZ', '🇹🇿', '255'),
    _c('Thailand', 'TH', '🇹🇭', '66'),
    _c('Timor-Leste', 'TL', '🇹🇱', '670'),
    _c('Togo', 'TG', '🇹🇬', '228'),
    _c('Tokelau', 'TK', '🇹🇰', '690'),
    _c('Tonga', 'TO', '🇹🇴', '676'),
    _c('Trinidad and Tobago', 'TT', '🇹🇹', '1868'),
    _c('Tunisia', 'TN', '🇹🇳', '216'),
    _c('Turkey', 'TR', '🇹🇷', '90'),
    _c('Turkmenistan', 'TM', '🇹🇲', '993'),
    _c('Turks and Caicos Islands', 'TC', '🇹🇨', '1649'),
    _c('Tuvalu', 'TV', '🇹🇻', '688'),
    _c('Uganda', 'UG', '🇺🇬', '256'),
    _c('Ukraine', 'UA', '🇺🇦', '380'),
    _c('United Arab Emirates', 'AE', '🇦🇪', '971'),
    _c('United Kingdom', 'GB', '🇬🇧', '44'),
    _c('United States', 'US', '🇺🇸', '1'),
    _c('Uruguay', 'UY', '🇺🇾', '598'),
    _c('US Virgin Islands', 'VI', '🇻🇮', '1340'),
    _c('Uzbekistan', 'UZ', '🇺🇿', '998'),
    _c('Vanuatu', 'VU', '🇻🇺', '678'),
    _c('Vatican City', 'VA', '🇻🇦', '39'),
    _c('Venezuela', 'VE', '🇻🇪', '58'),
    _c('Vietnam', 'VN', '🇻🇳', '84'),
    _c('Yemen', 'YE', '🇾🇪', '967'),
    _c('Zambia', 'ZM', '🇿🇲', '260'),
    _c('Zimbabwe', 'ZW', '🇿🇼', '263'),
  ];

  /// Case-insensitive lookup by ISO 3166-1 alpha-2 code.
  static CountryCode? byIso(String isoCode) {
    final target = isoCode.trim().toUpperCase();
    for (final country in all) {
      if (country.isoCode == target) return country;
    }
    return null;
  }

  /// Lookup by dialing code (with or without the leading `+`). When several
  /// countries share a dialing code (e.g. `+1` for US/Canada, `+39` for
  /// Italy/Vatican) the first match is returned.
  static CountryCode? byDialCode(String dialCode) {
    final target = dialCode.replaceAll(RegExp(r'[^0-9]'), '');
    if (target.isEmpty) return null;
    for (final country in all) {
      if (country.dialCode == target) return country;
    }
    return null;
  }

  /// Countries whose name, dial code or ISO code contains [query]
  /// (case-insensitive). Returns every matching entry in alphabetical order.
  ///
  /// Dial-code queries tolerate a leading `+` (e.g. `+971` finds UAE); the
  /// digits are stripped from the query before comparing against the stored
  /// dial code.
  static List<CountryCode> search(String query) {
    final q = query.trim().toLowerCase();
    if (q.isEmpty) return all;
    final digits = q.replaceAll(RegExp(r'[^0-9]'), '');
    return all.where((country) {
      return country.name.toLowerCase().contains(q) ||
          (digits.isNotEmpty && country.dialCode.contains(digits)) ||
          country.isoCode.toLowerCase().contains(q);
    }).toList();
  }
}
