# UI Specifications

## Overview

This document tracks UI/UX improvements for the LISA overlay interface.

## Completed

### 1. Top Edge Fade Effect
**Status:** Completed

Added an opacity mask to the chat ScrollViewer that fades content at the top edge. Messages that scroll out of view now fade out smoothly instead of being harshly cropped.

**Implementation:**
- `OpacityMask` with `LinearGradientBrush` on ScrollViewer
- Transparent at 0%, fully opaque at 6%
- Works across all themes including Glass

**Files Modified:**
- `Host.Win/Views/OverlayWindow.xaml`

---

### 2. Reduced Scroll Speed
**Status:** Completed

Custom scroll handling that reduces scroll velocity to 25% of default. Users can now fine-scroll through messages with trackpad or mouse wheel without jumping multiple messages at once.

**Implementation:**
- `PreviewMouseWheel` event handler intercepts scroll input
- Scroll delta multiplied by 0.25 for fine control
- Direct offset setting (no momentum/drift)

**Files Modified:**
- `Host.Win/Views/OverlayWindow.xaml.cs`

---

### 3. UI Depth Enhancement
**Status:** Completed

Enhanced visual depth following Windows 11 Fluent design principles with layered surfaces and subtle borders.

**Implementation:**
- Added `CornerRadius="12"` to RootBorder for consistent rounded corners matching shadow layer
- Added subtle 1px border to inner content panel using `OverlayBorder` color
- Added 1px border to user chat bubbles using `UserBubbleBorder` for depth consistency
- Assistant bubbles already had borders; user bubbles now match the visual weight

**Files Modified:**
- `Host.Win/Views/OverlayWindow.xaml`

---

## Pending

None at this time.

---

## Design Guidelines

### Color System
- **Dark Theme:** Elevation via brightness (#202020 base to #454545 elevated)
- **Light Theme:** Elevation via shadows and subtle grays
- **Glass Theme:** Layered transparency with 50-85% opacity levels
- **Green Theme:** Emerald tones with teal accents

### Spacing
- Base unit: 4px
- Standard corner radius: 8px
- Chat bubble corner radius: 12px
- Chat bubble padding: 10px
- Chat bubble margin: 6px vertical

### Typography
- Font family: Segoe UI (Segoe UI Variable preferred)
- Base size: 14px
- Sender name: SemiBold weight

### Scrolling
- Scroll speed: 25% of system default
- Top fade: 6% of viewport height
