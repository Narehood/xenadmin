/* Copyright (c) XCP-ng Project. 
 * 
 * Redistribution and use in source and binary forms, 
 * with or without modification, are permitted provided 
 * that the following conditions are met: 
 * 
 * *   Redistributions of source code must retain the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer. 
 * *   Redistributions in binary form must reproduce the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer in the documentation and/or other 
 *     materials provided with the distribution. 
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND 
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, 
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF 
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR 
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, 
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, 
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR 
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, 
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING 
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE 
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF 
 * SUCH DAMAGE.
 */

using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using XenAdmin.Controls.TabControl.TabStyleProviders;

namespace XenAdmin.Controls.TabControl
{
    [ToolboxItem(false)]
    public abstract class TabStyleProvider : Component
    {
        #region Constructor

        protected TabStyleProvider(CustomTabControl tabControl)
        {
            _TabControl = tabControl;

            _BorderColor = Color.Empty;
            _BorderColorSelected = Color.Empty;
            _FocusColor = Color.Orange;

            if (_TabControl.RightToLeftLayout)
                _ImageAlign = ContentAlignment.MiddleRight;
            else
                _ImageAlign = ContentAlignment.MiddleLeft;

            HotTrack = true;

            //	Must set after the _Overlap as this is used in the calculations of the actual padding
            Padding = new Point(6, 3);
        }

        #endregion

        #region Factory Methods

        public static TabStyleProvider CreateProvider(CustomTabControl tabControl)
        {
            TabStyleProvider provider;

            //	Depending on the display style of the tabControl generate an appropriate provider.
            switch (tabControl.DisplayStyle)
            {
                case TabStyle.Default:
                    provider = new TabStyleDefaultProvider(tabControl);
                    break;

                case TabStyle.Angled:
                    provider = new TabStyleAngledProvider(tabControl);
                    break;

                default:
                    provider = new TabStyleDefaultProvider(tabControl);
                    break;
            }

            provider._Style = tabControl.DisplayStyle;
            return provider;
        }

        #endregion

        #region Tab border and rect

        public GraphicsPath GetTabBorder(int index)
        {
            var path = new GraphicsPath();
            var tabBounds = GetTabRect(index);

            AddTabBorder(path, tabBounds);

            path.CloseFigure();
            return path;
        }

        #endregion

        #region Protected variables

        [SuppressMessage("Microsoft.Design", "CA1051:DoNotDeclareVisibleInstanceFields")]
        protected CustomTabControl _TabControl;

        [SuppressMessage("Microsoft.Design", "CA1051:DoNotDeclareVisibleInstanceFields")]
        protected Point _Padding;

        [SuppressMessage("Microsoft.Design", "CA1051:DoNotDeclareVisibleInstanceFields")]
        protected bool _HotTrack;

        [SuppressMessage("Microsoft.Design", "CA1051:DoNotDeclareVisibleInstanceFields")]
        protected TabStyle _Style = TabStyle.Default;


        [SuppressMessage("Microsoft.Design", "CA1051:DoNotDeclareVisibleInstanceFields")]
        protected ContentAlignment _ImageAlign;

        [SuppressMessage("Microsoft.Design", "CA1051:DoNotDeclareVisibleInstanceFields")]
        protected int _Radius = 1;

        [SuppressMessage("Microsoft.Design", "CA1051:DoNotDeclareVisibleInstanceFields")]
        protected int _Overlap;

        [SuppressMessage("Microsoft.Design", "CA1051:DoNotDeclareVisibleInstanceFields")]
        protected bool _FocusTrack;

        [SuppressMessage("Microsoft.Design", "CA1051:DoNotDeclareVisibleInstanceFields")]
        protected float _Opacity = 1;

        [SuppressMessage("Microsoft.Design", "CA1051:DoNotDeclareVisibleInstanceFields")]
        protected Color _BorderColorSelected = Color.Empty;

        [SuppressMessage("Microsoft.Design", "CA1051:DoNotDeclareVisibleInstanceFields")]
        protected Color _BorderColor = Color.Empty;

        [SuppressMessage("Microsoft.Design", "CA1051:DoNotDeclareVisibleInstanceFields")]
        protected Color _BorderColorHot = Color.Empty;

        [SuppressMessage("Microsoft.Design", "CA1051:DoNotDeclareVisibleInstanceFields")]
        protected Color _FocusColor = Color.Empty;

        [SuppressMessage("Microsoft.Design", "CA1051:DoNotDeclareVisibleInstanceFields")]
        protected Color _TextColor = Color.Empty;

        [SuppressMessage("Microsoft.Design", "CA1051:DoNotDeclareVisibleInstanceFields")]
        protected Color _TextColorSelected = Color.Empty;

        [SuppressMessage("Microsoft.Design", "CA1051:DoNotDeclareVisibleInstanceFields")]
        protected Color _TextColorDisabled = Color.Empty;

        #endregion

        #region overridable Methods

        public abstract void AddTabBorder(GraphicsPath path, Rectangle tabBounds);

        public virtual Rectangle GetTabRect(int index)
        {
            if (index < 0) return new Rectangle();
            var tabBounds = _TabControl.GetTabRect(index);
            if (_TabControl.RightToLeftLayout) tabBounds.X = _TabControl.Width - tabBounds.Right;
            var firstTabinRow = _TabControl.IsFirstTabInRow(index);

            //	Expand to overlap the tabpage
            switch (_TabControl.Alignment)
            {
                case TabAlignment.Top:
                    tabBounds.Height += 2;
                    break;
                case TabAlignment.Bottom:
                    tabBounds.Height += 2;
                    tabBounds.Y -= 2;
                    break;
                case TabAlignment.Left:
                    tabBounds.Width += 2;
                    break;
                case TabAlignment.Right:
                    tabBounds.X -= 2;
                    tabBounds.Width += 2;
                    break;
            }


            //	Greate Overlap unless first tab in the row to align with tabpage
            if ((!firstTabinRow || _TabControl.RightToLeftLayout) && _Overlap > 0)
            {
                if (_TabControl.Alignment <= TabAlignment.Bottom)
                {
                    tabBounds.X -= _Overlap;
                    tabBounds.Width += _Overlap;
                }
                else
                {
                    tabBounds.Y -= _Overlap;
                    tabBounds.Height += _Overlap;
                }
            }

            //	Adjust first tab in the row to align with tabpage
            EnsureFirstTabIsInView(ref tabBounds, index);

            return tabBounds;
        }


        [SuppressMessage("Microsoft.Design", "CA1045:DoNotPassTypesByReference", MessageId = "0#")]
        protected virtual void EnsureFirstTabIsInView(ref Rectangle tabBounds, int index)
        {
            //	Adjust first tab in the row to align with tabpage
            //	Make sure we only reposition visible tabs, as we may have scrolled out of view.

            var firstTabinRow = _TabControl.IsFirstTabInRow(index);

            if (firstTabinRow)
            {
                if (_TabControl.Alignment <= TabAlignment.Bottom)
                {
                    if (_TabControl.RightToLeftLayout)
                    {
                        if (tabBounds.Left < _TabControl.Right)
                        {
                            var tabPageRight = _TabControl.GetPageBounds(index).Right;
                            if (tabBounds.Right > tabPageRight) tabBounds.Width -= tabBounds.Right - tabPageRight;
                        }
                    }
                    else
                    {
                        if (tabBounds.Right > 0)
                        {
                            var tabPageX = _TabControl.GetPageBounds(index).X;
                            if (tabBounds.X < tabPageX)
                            {
                                tabBounds.Width -= tabPageX - tabBounds.X;
                                tabBounds.X = tabPageX;
                            }
                        }
                    }
                }
                else
                {
                    if (_TabControl.RightToLeftLayout)
                    {
                        if (tabBounds.Top < _TabControl.Bottom)
                        {
                            var tabPageBottom = _TabControl.GetPageBounds(index).Bottom;
                            if (tabBounds.Bottom > tabPageBottom) tabBounds.Height -= tabBounds.Bottom - tabPageBottom;
                        }
                    }
                    else
                    {
                        if (tabBounds.Bottom > 0)
                        {
                            var tabPageY = _TabControl.GetPageBounds(index).Location.Y;
                            if (tabBounds.Y < tabPageY)
                            {
                                tabBounds.Height -= tabPageY - tabBounds.Y;
                                tabBounds.Y = tabPageY;
                            }
                        }
                    }
                }
            }
        }

        protected virtual Brush GetTabBackgroundBrush(int index)
        {
            LinearGradientBrush fillBrush = null;

            //	Capture the colours dependant on selection state of the tab
            var dark = Color.FromArgb(207, 207, 207);
            var light = Color.FromArgb(242, 242, 242);

            if (_TabControl.SelectedIndex == index)
            {
                dark = SystemColors.ControlLight;
                light = SystemColors.Window;
            }
            else if (!_TabControl.TabPages[index].Enabled)
            {
                light = dark;
            }
            else if (_HotTrack && index == _TabControl.ActiveIndex)
            {
                //	Enable hot tracking
                light = Color.FromArgb(234, 246, 253);
                dark = Color.FromArgb(167, 217, 245);
            }

            //	Get the correctly aligned gradient
            var tabBounds = GetTabRect(index);
            tabBounds.Inflate(3, 3);
            tabBounds.X -= 1;
            tabBounds.Y -= 1;
            switch (_TabControl.Alignment)
            {
                case TabAlignment.Top:
                    if (_TabControl.SelectedIndex == index) dark = light;
                    fillBrush = new LinearGradientBrush(tabBounds, light, dark, LinearGradientMode.Vertical);
                    break;
                case TabAlignment.Bottom:
                    fillBrush = new LinearGradientBrush(tabBounds, light, dark, LinearGradientMode.Vertical);
                    break;
                case TabAlignment.Left:
                    fillBrush = new LinearGradientBrush(tabBounds, dark, light, LinearGradientMode.Horizontal);
                    break;
                case TabAlignment.Right:
                    fillBrush = new LinearGradientBrush(tabBounds, light, dark, LinearGradientMode.Horizontal);
                    break;
            }

            //	Add the blend
            fillBrush.Blend = GetBackgroundBlend();

            return fillBrush;
        }

        #endregion

        #region Base Properties

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public TabStyle DisplayStyle
        {
            get => _Style;
            set => _Style = value;
        }

        [Category("Appearance")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public ContentAlignment ImageAlign
        {
            get => _ImageAlign;
            set
            {
                _ImageAlign = value;
                _TabControl.Invalidate();
            }
        }

        [Category("Appearance")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public Point Padding
        {
            get => _Padding;
            set
            {
                _Padding = value;
                //	This line will trigger the handle to recreate, therefore invalidating the control

                if (value.X + _Radius / 2 < 1)
                    ((System.Windows.Forms.TabControl)_TabControl).Padding = new Point(0, value.Y);
                else
                    ((System.Windows.Forms.TabControl)_TabControl).Padding =
                        new Point(value.X + _Radius / 2 - 1, value.Y);
            }
        }


        [Category("Appearance")]
        [DefaultValue(1)]
        [Browsable(true)]
        public int Radius
        {
            get => _Radius;
            set
            {
                if (value < 1) throw new ArgumentException("The radius must be greater than 1", "value");
                _Radius = value;
                //	Adjust padding
                Padding = _Padding;
            }
        }

        [Category("Appearance")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public int Overlap
        {
            get => _Overlap;
            set
            {
                if (value < 0) throw new ArgumentException("The tabs cannot have a negative overlap", "value");
                _Overlap = value;
            }
        }


        [Category("Appearance")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool FocusTrack
        {
            get => _FocusTrack;
            set
            {
                _FocusTrack = value;
                _TabControl.Invalidate();
            }
        }

        [Category("Appearance")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool HotTrack
        {
            get => _HotTrack;
            set
            {
                _HotTrack = value;
                ((System.Windows.Forms.TabControl)_TabControl).HotTrack = value;
            }
        }

        [Category("Appearance")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public float Opacity
        {
            get => _Opacity;
            set
            {
                if (value < 0) throw new ArgumentException("The opacity must be between 0 and 1", "value");
                if (value > 1) throw new ArgumentException("The opacity must be between 0 and 1", "value");
                _Opacity = value;
                _TabControl.Invalidate();
            }
        }

        [Category("Appearance")]
        [DefaultValue(typeof(Color), "")]
        public Color BorderColorSelected
        {
            get
            {
                if (_BorderColorSelected.IsEmpty)
                    return ThemedColors.ToolBorder;
                return _BorderColorSelected;
            }
            set
            {
                if (value.Equals(ThemedColors.ToolBorder))
                    _BorderColorSelected = Color.Empty;
                else
                    _BorderColorSelected = value;
                _TabControl.Invalidate();
            }
        }

        [Category("Appearance")]
        [DefaultValue(typeof(Color), "")]
        public Color BorderColorHot
        {
            get
            {
                if (_BorderColorHot.IsEmpty)
                    return SystemColors.ControlDark;
                return _BorderColorHot;
            }
            set
            {
                if (value.Equals(SystemColors.ControlDark))
                    _BorderColorHot = Color.Empty;
                else
                    _BorderColorHot = value;
                _TabControl.Invalidate();
            }
        }

        [Category("Appearance")]
        [DefaultValue(typeof(Color), "")]
        public Color BorderColor
        {
            get
            {
                if (_BorderColor.IsEmpty)
                    return SystemColors.ControlDark;
                return _BorderColor;
            }
            set
            {
                if (value.Equals(SystemColors.ControlDark))
                    _BorderColor = Color.Empty;
                else
                    _BorderColor = value;
                _TabControl.Invalidate();
            }
        }

        [Category("Appearance")]
        [DefaultValue(typeof(Color), "")]
        public Color TextColor
        {
            get
            {
                if (_TextColor.IsEmpty)
                    return SystemColors.ControlText;
                return _TextColor;
            }
            set
            {
                if (value.Equals(SystemColors.ControlText))
                    _TextColor = Color.Empty;
                else
                    _TextColor = value;
                _TabControl.Invalidate();
            }
        }

        [Category("Appearance")]
        [DefaultValue(typeof(Color), "")]
        public Color TextColorSelected
        {
            get
            {
                if (_TextColorSelected.IsEmpty)
                    return SystemColors.ControlText;
                return _TextColorSelected;
            }
            set
            {
                if (value.Equals(SystemColors.ControlText))
                    _TextColorSelected = Color.Empty;
                else
                    _TextColorSelected = value;
                _TabControl.Invalidate();
            }
        }

        [Category("Appearance")]
        [DefaultValue(typeof(Color), "")]
        public Color TextColorDisabled
        {
            get
            {
                if (_TextColor.IsEmpty)
                    return SystemColors.ControlDark;
                return _TextColorDisabled;
            }
            set
            {
                if (value.Equals(SystemColors.ControlDark))
                    _TextColorDisabled = Color.Empty;
                else
                    _TextColorDisabled = value;
                _TabControl.Invalidate();
            }
        }


        [Category("Appearance")]
        [DefaultValue(typeof(Color), "Orange")]
        public Color FocusColor
        {
            get => _FocusColor;
            set
            {
                _FocusColor = value;
                _TabControl.Invalidate();
            }
        }

        #endregion

        #region Painting

        public void PaintTab(int index, Graphics graphics)
        {
            using (var tabpath = GetTabBorder(index))
            {
                using (var fillBrush = GetTabBackgroundBrush(index))
                {
                    //	Paint the background
                    graphics.FillPath(fillBrush, tabpath);

                    //	Paint a focus indication
                    if (_TabControl.Focused) DrawTabFocusIndicator(tabpath, index, graphics);
                }
            }
        }

        private void DrawTabFocusIndicator(GraphicsPath tabpath, int index, Graphics graphics)
        {
            if (_FocusTrack && _TabControl.Focused && index == _TabControl.SelectedIndex)
            {
                Brush focusBrush = null;
                var pathRect = tabpath.GetBounds();
                var focusRect = Rectangle.Empty;
                switch (_TabControl.Alignment)
                {
                    case TabAlignment.Top:
                        focusRect = new Rectangle((int)pathRect.X, (int)pathRect.Y, (int)pathRect.Width, 4);
                        focusBrush = new LinearGradientBrush(focusRect, _FocusColor, SystemColors.Window,
                            LinearGradientMode.Vertical);
                        break;
                    case TabAlignment.Bottom:
                        focusRect = new Rectangle((int)pathRect.X, (int)pathRect.Bottom - 4, (int)pathRect.Width, 4);
                        focusBrush = new LinearGradientBrush(focusRect, SystemColors.ControlLight, _FocusColor,
                            LinearGradientMode.Vertical);
                        break;
                    case TabAlignment.Left:
                        focusRect = new Rectangle((int)pathRect.X, (int)pathRect.Y, 4, (int)pathRect.Height);
                        focusBrush = new LinearGradientBrush(focusRect, _FocusColor, SystemColors.ControlLight,
                            LinearGradientMode.Horizontal);
                        break;
                    case TabAlignment.Right:
                        focusRect = new Rectangle((int)pathRect.Right - 4, (int)pathRect.Y, 4, (int)pathRect.Height);
                        focusBrush = new LinearGradientBrush(focusRect, SystemColors.ControlLight, _FocusColor,
                            LinearGradientMode.Horizontal);
                        break;
                }

                //	Ensure the focus stip does not go outside the tab
                var focusRegion = new Region(focusRect);
                focusRegion.Intersect(tabpath);
                graphics.FillRegion(focusBrush, focusRegion);
                focusRegion.Dispose();
                focusBrush.Dispose();
            }
        }

        #endregion

        #region Background brushes

        private Blend GetBackgroundBlend()
        {
            float[] relativeIntensities = { 0f, 0.7f, 1f };
            float[] relativePositions = { 0f, 0.6f, 1f };

            //	Glass look to top aligned tabs
            if (_TabControl.Alignment == TabAlignment.Top)
            {
                relativeIntensities = new[] { 0f, 0.5f, 1f, 1f };
                relativePositions = new[] { 0f, 0.5f, 0.51f, 1f };
            }

            var blend = new Blend();
            blend.Factors = relativeIntensities;
            blend.Positions = relativePositions;

            return blend;
        }

        public virtual Brush GetPageBackgroundBrush(int index)
        {
            //	Capture the colours dependant on selection state of the tab
            var light = Color.FromArgb(242, 242, 242);
            if (_TabControl.Alignment == TabAlignment.Top) light = Color.FromArgb(207, 207, 207);

            if (_TabControl.SelectedIndex == index)
                light = SystemColors.Window;
            else if (!_TabControl.TabPages[index].Enabled)
                light = Color.FromArgb(207, 207, 207);
            else if (_HotTrack && index == _TabControl.ActiveIndex)
                //	Enable hot tracking
                light = Color.FromArgb(234, 246, 253);

            return new SolidBrush(light);
        }

        #endregion
    }
}