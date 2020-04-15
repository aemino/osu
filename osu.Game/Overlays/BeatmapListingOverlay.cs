﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Threading;
using osu.Game.Audio;
using osu.Game.Beatmaps;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Overlays.BeatmapListing;
using osu.Game.Overlays.Direct;
using osu.Game.Rulesets;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Overlays
{
    public class BeatmapListingOverlay : FullscreenOverlay
    {
        /// <summary>
        /// Scroll distance from bottom at which new beatmaps will be loaded, if possible.
        /// </summary>
        protected const int pagination_scroll_distance = 500;

        [Resolved]
        private PreviewTrackManager previewTrackManager { get; set; }

        [Resolved]
        private RulesetStore rulesets { get; set; }

        private OverlayScrollContainer scroll;

        private Drawable currentContent;
        private BeatmapListingSearchSection searchSection;
        private BeatmapListingSortTabControl sortControl;

        public BeatmapListingOverlay()
            : base(OverlayColourScheme.Blue)
        {
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = ColourProvider.Background6
                },
                scroll = new OverlayScrollContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    ScrollbarVisible = false,
                    Child = new ReverseChildIDFillFlowContainer<Drawable>
                    {
                        AutoSizeAxes = Axes.Y,
                        RelativeSizeAxes = Axes.X,
                        Direction = FillDirection.Vertical,
                        Spacing = new Vector2(0, 10),
                        Children = new Drawable[]
                        {
                            new FillFlowContainer
                            {
                                AutoSizeAxes = Axes.Y,
                                RelativeSizeAxes = Axes.X,
                                Direction = FillDirection.Vertical,
                                Masking = true,
                                EdgeEffect = new EdgeEffectParameters
                                {
                                    Colour = Color4.Black.Opacity(0.25f),
                                    Type = EdgeEffectType.Shadow,
                                    Radius = 3,
                                    Offset = new Vector2(0f, 1f),
                                },
                                Children = new Drawable[]
                                {
                                    new BeatmapListingHeader(),
                                    searchSection = new BeatmapListingSearchSection(),
                                }
                            },
                            new Container
                            {
                                AutoSizeAxes = Axes.Y,
                                RelativeSizeAxes = Axes.X,
                                Children = new Drawable[]
                                {
                                    new Box
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Colour = ColourProvider.Background4,
                                    },
                                    new FillFlowContainer
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        AutoSizeAxes = Axes.Y,
                                        Children = new Drawable[]
                                        {
                                            new Container
                                            {
                                                RelativeSizeAxes = Axes.X,
                                                Height = 40,
                                                Children = new Drawable[]
                                                {
                                                    new Box
                                                    {
                                                        RelativeSizeAxes = Axes.Both,
                                                        Colour = ColourProvider.Background5
                                                    },
                                                    sortControl = new BeatmapListingSortTabControl
                                                    {
                                                        Anchor = Anchor.CentreLeft,
                                                        Origin = Anchor.CentreLeft,
                                                        Margin = new MarginPadding { Left = 20 }
                                                    }
                                                }
                                            },
                                            new Container
                                            {
                                                AutoSizeAxes = Axes.Y,
                                                RelativeSizeAxes = Axes.X,
                                                Padding = new MarginPadding { Horizontal = 20 },
                                                Children = new Drawable[]
                                                {
                                                    panelTarget = new Container
                                                    {
                                                        AutoSizeAxes = Axes.Y,
                                                        RelativeSizeAxes = Axes.X,
                                                    },
                                                    loadingLayer = new LoadingLayer(panelTarget),
                                                }
                                            },
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            var sortCriteria = sortControl.Current;
            var sortDirection = sortControl.SortDirection;

            searchSection.Query.BindValueChanged(query =>
            {
                sortCriteria.Value = string.IsNullOrEmpty(query.NewValue) ? DirectSortCriteria.Ranked : DirectSortCriteria.Relevance;
                sortDirection.Value = SortDirection.Descending;

                queueUpdateSearch(true);
            });

            searchSection.Ruleset.BindValueChanged(_ => queueUpdateSearch());
            searchSection.Category.BindValueChanged(_ => queueUpdateSearch());
            sortCriteria.BindValueChanged(_ => queueUpdateSearch());
            sortDirection.BindValueChanged(_ => queueUpdateSearch());
        }

        private ScheduledDelegate queryChangedDebounce;
        private ScheduledDelegate addPageDebounce;

        private LoadingLayer loadingLayer;
        private Container panelTarget;

        [CanBeNull]
        private BeatmapSetPager beatmapSetPager;

        private bool shouldLoadNextPage => scroll.ScrollableExtent > 0 && scroll.IsScrolledToEnd(pagination_scroll_distance);

        private void queueUpdateSearch(bool queryTextChanged = false)
        {
            beatmapSetPager?.Reset();

            queryChangedDebounce?.Cancel();
            queryChangedDebounce = Scheduler.AddDelayed(updateSearch, queryTextChanged ? 500 : 100);
        }

        private void queueAddPage()
        {
            if (beatmapSetPager == null || !beatmapSetPager.CanFetchNextPage)
                return;

            if (addPageDebounce != null)
                return;

            beatmapSetPager.FetchNextPage();
        }

        private void updateSearch()
        {
            if (!IsLoaded)
                return;

            if (State.Value == Visibility.Hidden)
                return;

            if (API == null)
                return;

            previewTrackManager.StopAnyPlaying(this);

            loadingLayer.Show();

            beatmapSetPager?.Reset();
            beatmapSetPager = new BeatmapSetPager(
                API,
                rulesets,
                searchSection.Query.Value,
                searchSection.Ruleset.Value,
                searchSection.Category.Value,
                sortControl.Current.Value,
                sortControl.SortDirection.Value);

            beatmapSetPager.PageFetch += onPageFetch;

            addPageDebounce?.Cancel();
            addPageDebounce = null;

            queueAddPage();
        }

        private void onPageFetch(List<BeatmapSetInfo> beatmaps)
        {
            Schedule(() =>
            {
                if (beatmapSetPager.IsPastFirstPage)
                {
                    addPanels(beatmaps);
                }
                else
                {
                    recreatePanels(beatmaps);
                }

                addPageDebounce = Scheduler.AddDelayed(() => addPageDebounce = null, 1000);
            });
        }

        private IEnumerable<DirectPanel> createPanels(IEnumerable<BeatmapSetInfo> beatmaps)
        {
            return beatmaps.Select<BeatmapSetInfo, DirectPanel>(b => new DirectGridPanel(b)
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
            });
        }

        private void recreatePanels(IEnumerable<BeatmapSetInfo> beatmaps)
        {
            if (beatmapSetPager.TotalSets == 0)
            {
                searchSection.BeatmapSet = null;
                LoadComponentAsync(new NotFoundDrawable(), addContentToPlaceholder);
                return;
            }

            var newPanels = new FillFlowContainer<DirectPanel>
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Spacing = new Vector2(10),
                Alpha = 0,
                Margin = new MarginPadding { Vertical = 15 },
                ChildrenEnumerable = createPanels(beatmaps)
            };

            LoadComponentAsync(newPanels, loaded =>
            {
                addContentToPlaceholder(loaded);
                searchSection.BeatmapSet = beatmaps.First();
            });
        }

        private void addPanels(IEnumerable<BeatmapSetInfo> beatmaps)
        {
            LoadComponentsAsync(createPanels(beatmaps), loaded => addPanelsToContent(loaded));
        }

        private void addContentToPlaceholder(Drawable content)
        {
            loadingLayer.Hide();

            Drawable lastContent = currentContent;

            if (lastContent != null)
            {
                lastContent.FadeOut(100, Easing.OutQuint).Expire();

                // Consider the case when the new content is smaller than the last content.
                // If the auto-size computation is delayed until fade out completes, the background remain high for too long making the resulting transition to the smaller height look weird.
                // At the same time, if the last content's height is bypassed immediately, there is a period where the new content is at Alpha = 0 when the auto-sized height will be 0.
                // To resolve both of these issues, the bypass is delayed until a point when the content transitions (fade-in and fade-out) overlap and it looks good to do so.
                lastContent.Delay(25).Schedule(() => lastContent.BypassAutoSizeAxes = Axes.Y);
            }

            panelTarget.Add(currentContent = content);
            currentContent.FadeIn(200, Easing.OutQuint);
        }

        private void addPanelsToContent(IEnumerable<DirectPanel> panels)
        {
            // TODO: Fade in?

            if (currentContent == null)
                return;

            ((FillFlowContainer<DirectPanel>)currentContent).AddRange(panels);
        }

        protected override void Update()
        {
            base.Update();

            if (shouldLoadNextPage)
                queueAddPage();
        }

        protected override void Dispose(bool isDisposing)
        {
            beatmapSetPager?.Reset();
            queryChangedDebounce?.Cancel();
            addPageDebounce?.Cancel();

            base.Dispose(isDisposing);
        }

        private class NotFoundDrawable : CompositeDrawable
        {
            public NotFoundDrawable()
            {
                RelativeSizeAxes = Axes.X;
                Height = 250;
                Alpha = 0;
                Margin = new MarginPadding { Top = 15 };
            }

            [BackgroundDependencyLoader]
            private void load(TextureStore textures)
            {
                AddInternal(new FillFlowContainer
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    RelativeSizeAxes = Axes.Y,
                    AutoSizeAxes = Axes.X,
                    Direction = FillDirection.Horizontal,
                    Spacing = new Vector2(10, 0),
                    Children = new Drawable[]
                    {
                        new Sprite
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            RelativeSizeAxes = Axes.Both,
                            FillMode = FillMode.Fit,
                            Texture = textures.Get(@"Online/not-found")
                        },
                        new OsuSpriteText
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            Text = @"... nope, nothing found.",
                        }
                    }
                });
            }
        }
    }
}
