import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

// Re-export the design tokens so screens that already depend on the shared UI
// primitives get colours, spacing, radii and typography from one import.
export 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';

/// DoodhDirect shared UI primitives.
///
/// This is the Phase 0 design-system surface that future screen work should
/// compose from. Every widget here is intentionally small and composable — the
/// aim is *not* one universal widget, but a consistent set of building blocks
/// that match the existing brand look (cream/teal/mint) and follow the proven
/// [`StatePanel`] approach used for loading/empty/error/offline states.
///
/// Accessibility: primitives expose semantic labels, keep 48px minimum touch
/// targets (via the theme button styles / explicit sizing) and use
/// contrast-safe semantic colour pairs from [`DoodhColors`].

// ---------------------------------------------------------------------------
// Tone
// ---------------------------------------------------------------------------

/// Semantic intent for chips, banners and status surfaces.
enum DoodhTone { success, warning, error, info, neutral }

/// Resolves a [`DoodhTone`] to its contrast-safe surface/foreground pair.
({Color surface, Color foreground}) doodhToneColors(DoodhTone tone) =>
    switch (tone) {
      DoodhTone.success => (
        surface: DoodhColors.successSurface,
        foreground: DoodhColors.successForeground,
      ),
      DoodhTone.warning => (
        surface: DoodhColors.warningSurface,
        foreground: DoodhColors.warningForeground,
      ),
      DoodhTone.error => (
        surface: DoodhColors.errorSurface,
        foreground: DoodhColors.errorForeground,
      ),
      DoodhTone.info => (
        surface: DoodhColors.infoSurface,
        foreground: DoodhColors.infoForeground,
      ),
      DoodhTone.neutral => (
        surface: DoodhColors.neutralSurface,
        foreground: DoodhColors.neutralForeground,
      ),
    };

/// Default leading icon used by [`DoodhInfoBanner`] for each tone.
IconData doodhToneIcon(DoodhTone tone) => switch (tone) {
  DoodhTone.success => Icons.check_circle_outline,
  DoodhTone.warning => Icons.warning_amber_rounded,
  DoodhTone.error => Icons.error_outline,
  DoodhTone.info => Icons.info_outline,
  DoodhTone.neutral => Icons.info_outline,
};

String _toneSemanticLabel(DoodhTone tone) => switch (tone) {
  DoodhTone.success => 'Success',
  DoodhTone.warning => 'Warning',
  DoodhTone.error => 'Error',
  DoodhTone.info => 'Information',
  DoodhTone.neutral => 'Information',
};

// ---------------------------------------------------------------------------
// Layout / responsive foundations
// ---------------------------------------------------------------------------

/// Builds a widget from the current window size bucket.
///
/// Prefer this over ad-hoc `LayoutBuilder` + magic breakpoint numbers so the
/// whole app shares [`DoodhBreakpoints`].
class DoodhResponsive extends StatelessWidget {
  const DoodhResponsive({super.key, required this.builder});

  final Widget Function(BuildContext context, DoodhWindowSize size) builder;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) =>
        builder(context, DoodhBreakpoints.of(constraints.maxWidth)),
  );
}

/// A responsive wrap-grid that reflows items based on available width.
///
/// Items keep at least [minItemWidth] and never exceed [maxColumns] columns,
/// so cards remain readable on mobile and dense on desktop/web.
class DoodhGrid extends StatelessWidget {
  const DoodhGrid({
    super.key,
    required this.children,
    this.spacing = DoodhSpacing.md,
    this.minItemWidth = 220,
    this.maxColumns = 4,
    this.minColumns = 1,
  });

  final List<Widget> children;
  final double spacing;
  final double minItemWidth;
  final int maxColumns;

  /// Floor on the column count so product grids stay 2-up on phones even
  /// when the width estimate would floor to 1 (narrow devices, larger text
  /// scaling). Defaults to 1 so non-product grids keep the old behaviour.
  final int minColumns;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final width = constraints.maxWidth;
      if (width <= 0 || children.isEmpty) {
        return const SizedBox.shrink();
      }
      // Account for the gutter between columns: without it the estimate is
      // one column short on phones (e.g. 328px / 170px floors to 1), which
      // forced single-column product lists on mobile widths.
      final columns = ((width + spacing) / (minItemWidth + spacing))
          .floor()
          .clamp(minColumns, maxColumns);
      final itemWidth = (width - spacing * (columns - 1)) / columns;
      return Wrap(
        spacing: spacing,
        runSpacing: spacing,
        children: [
          for (final child in children)
            SizedBox(width: itemWidth, child: child),
        ],
      );
    },
  );
}

// ---------------------------------------------------------------------------
// Surfaces
// ---------------------------------------------------------------------------

/// A standard bordered card with consistent padding.
class DoodhCard extends StatelessWidget {
  const DoodhCard({
    super.key,
    required this.child,
    this.padding = const EdgeInsets.all(DoodhSpacing.md),
    this.color,
    this.onTap,
    this.semanticLabel,
  });

  final Widget child;
  final EdgeInsetsGeometry padding;
  final Color? color;
  final VoidCallback? onTap;
  final String? semanticLabel;

  @override
  Widget build(BuildContext context) {
    final content = padding == EdgeInsets.zero
        ? child
        : Padding(padding: padding, child: child);
    Widget card = Card(
      color: color,
      clipBehavior: onTap == null ? Clip.none : Clip.antiAlias,
      child: onTap == null
          ? content
          : InkWell(onTap: onTap, borderRadius: DoodhRadii.mdRadius, child: content),
    );
    if (semanticLabel != null || onTap != null) {
      // Tappable cards are exposed as buttons so assistive tech (and Flutter
      // Web keyboard users) understand they are actionable.
      card = Semantics(
        label: semanticLabel,
        button: onTap != null,
        container: true,
        child: card,
      );
    }
    return card;
  }
}

/// Card grouping a titled section: optional header (icon, title, subtitle,
/// trailing) separated by a divider from the padded children.
class DoodhSectionCard extends StatelessWidget {
  const DoodhSectionCard({
    super.key,
    required this.children,
    this.icon,
    this.title,
    this.subtitle,
    this.trailing,
    this.padding = const EdgeInsets.all(DoodhSpacing.md),
  });

  final List<Widget> children;
  final IconData? icon;
  final String? title;
  final String? subtitle;
  final Widget? trailing;
  final EdgeInsetsGeometry padding;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final hasHeader = title != null || icon != null || trailing != null;
    return Card(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          if (hasHeader) ...[
            Padding(
              padding: const EdgeInsets.all(DoodhSpacing.md),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  if (icon != null) ...[
                    Icon(icon, color: theme.colorScheme.primary),
                    const SizedBox(width: 12),
                  ],
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        if (title != null)
                          Semantics(
                            header: true,
                            child: Text(
                              title!,
                              style: theme.textTheme.titleMedium,
                            ),
                          ),
                        if (subtitle != null) ...[
                          if (title != null) const SizedBox(height: DoodhSpacing.xs),
                          Text(
                            subtitle!,
                            style: theme.textTheme.bodySmall?.copyWith(
                              color: DoodhColors.muted,
                            ),
                          ),
                        ],
                      ],
                    ),
                  ),
                  if (trailing != null) ...[
                    const SizedBox(width: DoodhSpacing.sm),
                    Flexible(child: trailing!),
                  ],
                ],
              ),
            ),
            const Divider(height: 1),
          ],
          Padding(
            padding: padding,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: children,
            ),
          ),
        ],
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// Buttons
// ---------------------------------------------------------------------------

enum DoodhButtonVariant { primary, secondary }

/// Standard action button with optional leading icon and busy state.
///
/// Keeps the theme's 48px minimum touch target. Use [expand] to fill width.
/// Shared compact CTA style for narrow cards (see [DoodhButton.compact]).
/// Slimmer than the 48px full-size theme buttons but still a comfortable
/// touch target; the 13px label fits ~130px-wide 2-up product cards.
final ButtonStyle _compactStyle = ButtonStyle(
  minimumSize: WidgetStateProperty.all(const Size(0, 44)),
  padding: WidgetStateProperty.all(
    const EdgeInsets.symmetric(horizontal: 12),
  ),
  textStyle: WidgetStateProperty.all(
    const TextStyle(fontSize: 13, fontWeight: FontWeight.w700),
  ),
);

class DoodhButton extends StatelessWidget {
  const DoodhButton({
    super.key,
    required this.label,
    this.onPressed,
    this.icon,
    this.variant = DoodhButtonVariant.primary,
    this.expand = false,
    this.busy = false,
    this.compact = false,
  });

  final String label;
  final VoidCallback? onPressed;
  final IconData? icon;
  final DoodhButtonVariant variant;
  final bool expand;
  final bool busy;

  /// Slimmer button for narrow 2-up product cards: smaller icon, tighter
  /// padding and label so e.g. "Add to cart" fits a ~130px card without
  /// truncating. Full-size CTAs elsewhere are unchanged.
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final enabled = busy ? null : onPressed;
    final child = Row(
      mainAxisSize: MainAxisSize.min,
      mainAxisAlignment: MainAxisAlignment.center,
      children: [
        if (busy)
          const SizedBox(
            width: 16,
            height: 16,
            child: CircularProgressIndicator(strokeWidth: 2),
          )
        else if (icon != null)
          Icon(icon, size: compact ? 18 : 24),
        if (busy || icon != null)
          SizedBox(
            width: compact ? DoodhSpacing.xs : DoodhSpacing.sm,
          ),
        Flexible(child: Text(label, overflow: TextOverflow.ellipsis)),
      ],
    );
    final button = switch (variant) {
      DoodhButtonVariant.primary => FilledButton(
        onPressed: enabled,
        style: compact ? _compactStyle : null,
        child: child,
      ),
      DoodhButtonVariant.secondary => OutlinedButton(
        onPressed: enabled,
        style: compact ? _compactStyle : null,
        child: child,
      ),
    };
    final result = expand
        ? SizedBox(width: double.infinity, child: button)
        : button;
    if (!busy) {
      return result;
    }
    // The button is disabled for pointer input while busy, which would also
    // remove its semantic node; announce the in-progress state instead.
    return Semantics(
      label: '$label, in progress',
      button: true,
      enabled: false,
      liveRegion: true,
      child: ExcludeSemantics(child: result),
    );
  }
}

// ---------------------------------------------------------------------------
// Chips / pills
// ---------------------------------------------------------------------------

/// A compact pill chip. Use for statuses, tags and counters.
class DoodhChip extends StatelessWidget {
  const DoodhChip({
    super.key,
    required this.label,
    this.tone = DoodhTone.neutral,
    this.icon,
  });

  final String label;
  final DoodhTone tone;
  final IconData? icon;

  @override
  Widget build(BuildContext context) {
    final colors = doodhToneColors(tone);
    final textStyle = Theme.of(context).textTheme.labelSmall?.copyWith(
      color: colors.foreground,
      fontWeight: FontWeight.w700,
    );
    return Semantics(
      label: '${_toneSemanticLabel(tone)}: $label',
      container: true,
      child: ExcludeSemantics(
        child: DecoratedBox(
          decoration: BoxDecoration(
            color: colors.surface,
            borderRadius: BorderRadius.circular(DoodhRadii.pillValue),
          ),
          child: Padding(
            padding: const EdgeInsets.symmetric(
              horizontal: 10,
              vertical: DoodhSpacing.xs + 1,
            ),
            // Long labels in narrow cards shrink instead of overflowing.
            child: FittedBox(
              fit: BoxFit.scaleDown,
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  if (icon != null) ...[
                    Icon(icon, size: 14, color: colors.foreground),
                    const SizedBox(width: DoodhSpacing.xs),
                  ],
                  Text(label, style: textStyle),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// Banners / inline feedback
// ---------------------------------------------------------------------------

/// Inline, non-blocking feedback banner (saved / error / warning / info).
///
/// This replaces the duplicated private `_SavedBanner` / `_ErrorBanner` /
/// `_TestResultBanner` classes that were copied across feature screens.
class DoodhInfoBanner extends StatelessWidget {
  const DoodhInfoBanner({
    super.key,
    required this.message,
    this.tone = DoodhTone.info,
    this.title,
    this.icon,
    this.action,
    this.onDismiss,
  });

  final String message;
  final DoodhTone tone;
  final String? title;
  final IconData? icon;
  final Widget? action;
  final VoidCallback? onDismiss;

  @override
  Widget build(BuildContext context) {
    final colors = doodhToneColors(tone);
    final theme = Theme.of(context);
    return Semantics(
      liveRegion: true,
      label: '${_toneSemanticLabel(tone)}: $message',
      container: true,
      child: Card(
        color: colors.surface,
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Icon(icon ?? doodhToneIcon(tone), color: colors.foreground),
              const SizedBox(width: DoodhSpacing.sm),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    if (title != null)
                      Text(
                        title!,
                        style: theme.textTheme.titleSmall?.copyWith(
                          color: colors.foreground,
                        ),
                      ),
                    Text(message),
                  ],
                ),
              ),
              if (action != null) ...[
                const SizedBox(width: DoodhSpacing.sm),
                action!,
              ],
              if (onDismiss != null) ...[
                const SizedBox(width: DoodhSpacing.xs),
                IconButton(
                  tooltip: 'Dismiss',
                  onPressed: onDismiss,
                  icon: const Icon(Icons.close, size: 18),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

/// Success banner shown after a save. Thin wrapper over [`DoodhInfoBanner`].
class DoodhSavedBanner extends StatelessWidget {
  const DoodhSavedBanner({super.key, required this.message});

  final String message;

  @override
  Widget build(BuildContext context) =>
      DoodhInfoBanner(tone: DoodhTone.success, message: message);
}

/// Error banner for inline failures. Thin wrapper over [`DoodhInfoBanner`].
class DoodhErrorBanner extends StatelessWidget {
  const DoodhErrorBanner({super.key, required this.message});

  final String message;

  @override
  Widget build(BuildContext context) =>
      DoodhInfoBanner(tone: DoodhTone.error, message: message);
}

// ---------------------------------------------------------------------------
// Content rows / tiles
// ---------------------------------------------------------------------------

/// A label/value row with a fixed label column on wide layouts that stacks on
/// narrow ones. Replaces the repeated `_ProfileRow` / `_DetailRow` variants.
class DoodhKeyValueRow extends StatelessWidget {
  const DoodhKeyValueRow({
    super.key,
    required this.label,
    required this.value,
    this.labelWidth = 140,
    this.stackBelow = 420,
  });

  final String label;
  final String value;
  final double labelWidth;
  final double stackBelow;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final labelText = Text(
      label,
      style: theme.textTheme.bodyMedium?.copyWith(color: DoodhColors.muted),
    );
    final valueText = Text(value, style: theme.textTheme.bodyLarge);
    // Merge label + value into a single semantic node so screen readers read
    // "Label, value" rather than two unrelated fragments.
    return MergeSemantics(
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: DoodhSpacing.sm),
        child: LayoutBuilder(
          builder: (context, constraints) {
            if (constraints.maxWidth < stackBelow) {
              return Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  labelText,
                  const SizedBox(height: DoodhSpacing.xs),
                  valueText,
                ],
              );
            }
            return Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                SizedBox(width: labelWidth, child: labelText),
                const SizedBox(width: 12),
                Expanded(child: valueText),
              ],
            );
          },
        ),
      ),
    );
  }
}

/// A compact metric tile (icon, value, label). Replaces `_MetricTile`.
///
/// When [height] is provided the value is pushed to the bottom with a spacer,
/// matching the dashboard grid behaviour.
class DoodhMetricTile extends StatelessWidget {
  const DoodhMetricTile({
    super.key,
    required this.icon,
    required this.label,
    required this.value,
    this.height,
  });

  final IconData icon;
  final String label;
  final String value;
  final double? height;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final tile = Card(
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: height == null ? MainAxisSize.min : MainAxisSize.max,
          children: [
            Icon(icon, color: theme.colorScheme.primary),
            if (height != null) const Spacer() else const SizedBox(height: 12),
            Text(value, style: theme.textTheme.titleLarge),
            Text(
              label,
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: DoodhColors.muted,
              ),
            ),
          ],
        ),
      ),
    );
    return Semantics(
      label: '$label: $value',
      container: true,
      child: height == null ? tile : SizedBox(height: height, child: tile),
    );
  }
}

/// An icon + title + subtitle list tile. Replaces `_InfoTile` / `_ActionTile`.
class DoodhInfoTile extends StatelessWidget {
  const DoodhInfoTile({
    super.key,
    required this.icon,
    required this.title,
    required this.subtitle,
    this.onTap,
    this.trailing,
  });

  final IconData icon;
  final String title;
  final String subtitle;
  final VoidCallback? onTap;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) => ListTile(
    contentPadding: EdgeInsets.zero,
    leading: Icon(icon),
    title: Text(title),
    subtitle: Text(subtitle),
    trailing: trailing,
    onTap: onTap,
  );
}

// ---------------------------------------------------------------------------
// Inputs
// ---------------------------------------------------------------------------

/// A standard text form field wired to the shared input theme.
class DoodhField extends StatelessWidget {
  const DoodhField({
    super.key,
    required this.label,
    this.controller,
    this.hint,
    this.helperText,
    this.errorText,
    this.keyboardType,
    this.obscureText = false,
    this.enabled = true,
    this.maxLines = 1,
    this.prefixIcon,
    this.suffixIcon,
    this.textInputAction,
    this.autofillHints,
    this.inputFormatters,
    this.validator,
    this.onChanged,
    this.onSubmitted,
  });

  final String label;
  final TextEditingController? controller;
  final String? hint;
  final String? helperText;
  final String? errorText;
  final TextInputType? keyboardType;
  final bool obscureText;
  final bool enabled;
  final int maxLines;
  final Widget? prefixIcon;
  final Widget? suffixIcon;
  final TextInputAction? textInputAction;
  final Iterable<String>? autofillHints;
  final List<TextInputFormatter>? inputFormatters;
  final String? Function(String?)? validator;
  final ValueChanged<String>? onChanged;
  final ValueChanged<String>? onSubmitted;

  @override
  Widget build(BuildContext context) => TextFormField(
    controller: controller,
    keyboardType: keyboardType,
    obscureText: obscureText,
    enabled: enabled,
    maxLines: obscureText ? 1 : maxLines,
    textInputAction: textInputAction,
    autofillHints: autofillHints,
    inputFormatters: inputFormatters,
    validator: validator,
    onChanged: onChanged,
    onFieldSubmitted: onSubmitted,
    decoration: InputDecoration(
      labelText: label,
      hintText: hint,
      helperText: helperText,
      errorText: errorText,
      prefixIcon: prefixIcon,
      suffixIcon: suffixIcon,
    ),
  );
}

// ---------------------------------------------------------------------------
// Quantity / price
// ---------------------------------------------------------------------------

/// A minus / value / plus stepper with accessible, 48px touch targets.
class DoodhQuantityStepper extends StatelessWidget {
  const DoodhQuantityStepper({
    super.key,
    required this.quantity,
    required this.onChanged,
    this.min = 1,
    this.max = 99,
    this.semanticLabel = 'Quantity',
  });

  final int quantity;
  final ValueChanged<int> onChanged;
  final int min;
  final int max;
  final String semanticLabel;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Semantics(
      label: semanticLabel,
      value: '$quantity',
      container: true,
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          _StepperButton(
            icon: Icons.remove,
            tooltip: 'Decrease $semanticLabel',
            onPressed: quantity > min ? () => onChanged(quantity - 1) : null,
          ),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: DoodhSpacing.md),
            child: Text('$quantity', style: theme.textTheme.titleMedium),
          ),
          _StepperButton(
            icon: Icons.add,
            tooltip: 'Increase $semanticLabel',
            onPressed: quantity < max ? () => onChanged(quantity + 1) : null,
          ),
        ],
      ),
    );
  }
}

class _StepperButton extends StatelessWidget {
  const _StepperButton({
    required this.icon,
    required this.tooltip,
    required this.onPressed,
  });

  final IconData icon;
  final String tooltip;
  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) => SizedBox(
    width: 48,
    height: 48,
    child: IconButton.outlined(
      tooltip: tooltip,
      onPressed: onPressed,
      icon: Icon(icon, size: 18),
    ),
  );
}

/// Formats a price without noisy trailing zeros (`1200` not `1200.00`).
class DoodhPriceTag extends StatelessWidget {
  const DoodhPriceTag({
    super.key,
    required this.amount,
    this.currency = '\u20B9',
    this.originalAmount,
    this.style,
  });

  final num amount;
  final String currency;

  /// Optional struck-through original price (e.g. before a discount).
  final num? originalAmount;
  final TextStyle? style;

  static String format(num value) {
    final rounded = value.roundToDouble();
    if (rounded == value) return rounded.toInt().toString();
    return value.toStringAsFixed(2);
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final baseStyle = style ?? theme.textTheme.titleMedium;
    final semanticLabel = originalAmount == null
        ? 'Price $currency${format(amount)}'
        : 'Price $currency${format(amount)}, '
              'was $currency${format(originalAmount!)}';
    return Semantics(
      label: semanticLabel,
      container: true,
      child: ExcludeSemantics(
        // Narrow cards (e.g. 2-up phone grids) must shrink the combined
        // current+original price instead of overflowing.
        child: FittedBox(
          fit: BoxFit.scaleDown,
          child: Row(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.baseline,
            textBaseline: TextBaseline.alphabetic,
            children: [
              Text(
                '$currency${format(amount)}',
                style: baseStyle,
              ),
              if (originalAmount != null) ...[
                const SizedBox(width: DoodhSpacing.sm),
                Text(
                  '$currency${format(originalAmount!)}',
                  style: theme.textTheme.bodyMedium?.copyWith(
                    color: DoodhColors.muted,
                    decoration: TextDecoration.lineThrough,
                  ),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}
