using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace ZapretGui.Views.Controls
{
    /// <summary>Сегментированный переключатель вида «Выкл | TCP | UDP» (список кнопок в рамке).</summary>
    public partial class SegmentedControl : UserControl
    {
        private bool _syncing;

        public SegmentedControl()
        {
            InitializeComponent();
            SegmentList.Loaded += (_, __) => ApplyIndex();
            SegmentList.ItemContainerGenerator.StatusChanged += (_, __) => ApplyIndex();
            SegmentList.SelectionChanged += OnSelectionChanged;
        }

        public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
            nameof(ItemsSource), typeof(IEnumerable), typeof(SegmentedControl),
            new PropertyMetadata(null, OnItemsSourceChanged));

        public IEnumerable? ItemsSource
        {
            get => (IEnumerable?)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
            nameof(SelectedIndex), typeof(int), typeof(SegmentedControl),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedIndexChanged));

        public int SelectedIndex
        {
            get => (int)GetValue(SelectedIndexProperty);
            set => SetValue(SelectedIndexProperty, value);
        }

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((SegmentedControl)d).ApplyIndex();

        private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((SegmentedControl)d).ApplyIndex();

        private void ApplyIndex()
        {
            if (_syncing) return;
            try
            {
                _syncing = true;
                if (SegmentList.Items.Count == 0) return;
                var index = SelectedIndex;
                if (index < 0) index = 0;
                if (index >= SegmentList.Items.Count) index = SegmentList.Items.Count - 1;
                if (SegmentList.SelectedIndex != index) SegmentList.SelectedIndex = index;
            }
            finally
            {
                _syncing = false;
            }
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing) return;
            try
            {
                _syncing = true;
                SelectedIndex = SegmentList.SelectedIndex;
            }
            finally
            {
                _syncing = false;
            }
        }
    }
}
