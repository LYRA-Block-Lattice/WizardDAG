﻿using LyraWallet.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Xamarin.Forms;
using Xamarin.Forms.Xaml;

namespace LyraWallet.Views
{
	[XamlCompilation(XamlCompilationOptions.Compile)]
	public partial class CreateAccountPage : ContentPage
	{
		public CreateAccountPage ()
		{
            InitializeComponent ();

            var viewModel = new CreateAccountViewModel(this);
            viewModel.NetworkId = App.Store.State.Network;
            BindingContext = viewModel;
        }
    }
}